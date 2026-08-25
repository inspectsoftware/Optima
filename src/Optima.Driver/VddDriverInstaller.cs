using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Detection;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Driver.Providers;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.Logging;

namespace Optima.Driver;

/// <summary>
/// Installs the virtual display driver that ships alongside the application, so an end user
/// never opens Device Manager or runs devcon. The privileged steps (staging the package,
/// creating the root device node, writing the driver's settings file) all go through the
/// elevated helper behind a single UAC prompt.
/// </summary>
public sealed class VddDriverInstaller : IDriverInstaller
{
    /// <summary>Folder beside the executable that a driver package is shipped in.</summary>
    public const string BundledDriverFolder = "drivers";

    private readonly IElevationBroker _elevation;
    private readonly PnpDeviceLocator _deviceLocator;
    private readonly SettingsService _settings;
    private readonly ILogger<VddDriverInstaller> _logger;

    public VddDriverInstaller(
        IElevationBroker elevation,
        PnpDeviceLocator deviceLocator,
        SettingsService settings,
        ILogger<VddDriverInstaller> logger)
    {
        _elevation = elevation;
        _deviceLocator = deviceLocator;
        _settings = settings;
        _logger = logger;
    }

    public DriverPackageInfo? FindBundledPackage()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, BundledDriverFolder);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
        {
            try
            {
                var parsed = InfFile.Parse(File.ReadAllText(inf));
                if (string.IsNullOrWhiteSpace(parsed.HardwareId))
                {
                    _logger.LogWarning("Ignoring {Inf}: no hardware id could be read from it", inf);
                    continue;
                }

                var directory = Path.GetDirectoryName(inf)!;
                return new DriverPackageInfo
                {
                    InfPath = inf,
                    HardwareId = parsed.HardwareId,
                    Provider = parsed.Provider ?? string.Empty,
                    DisplayName = parsed.Description ?? Path.GetFileNameWithoutExtension(inf),
                    HasCatalog = Directory.EnumerateFiles(directory, "*.cat").Any(),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read driver package {Inf}", inf);
            }
        }
        return null;
    }

    public async Task<DriverState> GetStateAsync(CancellationToken ct = default)
    {
        var devices = await _deviceLocator.FindDisplayDevicesAsync(MttVddProvider.DeviceNameMarker, ct).ConfigureAwait(false);
        if (devices.Count > 0)
        {
            return DriverState.Installed;
        }
        return FindBundledPackage() is not null
            ? DriverState.NotInstalledPackageAvailable
            : DriverState.NotInstalledNoPackage;
    }

    public async Task<DriverInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var package = FindBundledPackage();
        if (package is null)
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "DRIVER_PACKAGE_MISSING",
                Title = "No virtual display driver is bundled with this build.",
                Explanation = $"Optima installs a driver from its '{BundledDriverFolder}' folder, but that folder is empty or absent.",
                SuggestedFixes =
                [
                    $"Place a virtual display driver package (.inf, .cat and its files) in the '{BundledDriverFolder}' folder next to Optima.exe",
                    "Or install a virtual display driver yourself — Optima will detect and use it",
                ],
            });
        }

        if (!package.HasCatalog)
        {
            _logger.LogWarning("Driver package {Inf} has no .cat catalog; Windows will very likely reject it", package.InfPath);
        }

        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "ELEVATION_DECLINED",
                Title = "Administrator access is required to install a display driver.",
                Explanation = "Installing a driver is a system-level change, so Windows requires approval.",
                SuggestedFixes = ["Choose Yes on the administrator prompt and try again"],
            });
        }

        _logger.LogInformation("Installing bundled driver {Name} ({HardwareId}) from {Inf}",
            package.DisplayName, package.HardwareId, package.InfPath);

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.InstallDriver,
            Args =
            {
                ["infPath"] = package.InfPath,
                ["hardwareId"] = package.HardwareId,
            },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "DRIVER_INSTALL_FAILED",
                Title = "The virtual display driver could not be installed.",
                Explanation = response.Error,
                SuggestedFixes =
                [
                    "Confirm the bundled driver package is digitally signed — Windows refuses unsigned driver packages",
                    "Check that the package targets 64-bit Windows 11",
                    "See the Logs page for the exact installer error",
                ],
                DeveloperDetails = $"inf: {package.InfPath}\nhardwareId: {package.HardwareId}\ncatalog present: {package.HasCatalog}",
            });
        }

        await EnsureSettingsFileAsync(ct).ConfigureAwait(false);

        var restartRequired = response.Data.GetValueOrDefault("restartRequired") == "1";
        _logger.LogInformation("Virtual display driver installed (restart required: {Restart})", restartRequired);
        return DriverInstallResult.Ok(restartRequired);
    }

    public async Task<DriverInstallResult> UninstallAsync(CancellationToken ct = default)
    {
        var package = FindBundledPackage();
        if (package is null)
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "DRIVER_PACKAGE_MISSING",
                Title = "Optima cannot remove a driver it did not install.",
                Explanation = "The bundled driver package is not present, so the hardware id to remove is unknown.",
                SuggestedFixes = ["Remove the device from Device Manager under Display adapters"],
            });
        }

        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "ELEVATION_DECLINED",
                Title = "Administrator access is required to remove a display driver.",
                Explanation = "Removing a device is a system-level change.",
                SuggestedFixes = ["Choose Yes on the administrator prompt and try again"],
            });
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.UninstallDriver,
            Args = { ["hardwareId"] = package.HardwareId },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            return DriverInstallResult.Fail(new UserFriendlyError
            {
                Code = "DRIVER_UNINSTALL_FAILED",
                Title = "The virtual display driver could not be removed.",
                Explanation = response.Error,
                SuggestedFixes = ["Remove the device from Device Manager under Display adapters"],
            });
        }

        _logger.LogInformation("Virtual display driver removed ({Count} device(s))", response.Data.GetValueOrDefault("removed"));
        return DriverInstallResult.Ok();
    }

    /// <summary>
    /// Writes the driver's settings file when it is missing. The driver reads it from a fixed
    /// location under C:\, which needs administrator rights to create — hence the helper.
    /// An existing file is never overwritten.
    /// </summary>
    private async Task EnsureSettingsFileAsync(CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        var path = string.IsNullOrWhiteSpace(settings.VddSettingsPath)
            ? MttVddProvider.DefaultSettingsPath
            : settings.VddSettingsPath;

        if (File.Exists(path))
        {
            return;
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.EnsureVddSettings,
            Args = { ["path"] = path, ["content"] = DefaultSettingsXml },
        }, ct).ConfigureAwait(false);

        if (response.Success)
        {
            _logger.LogInformation("Driver settings file ensured at {Path}", path);
        }
        else
        {
            _logger.LogWarning("Could not create the driver settings file at {Path}: {Error}", path, response.Error);
        }
    }

    /// <summary>
    /// Default mode list written on a fresh install. Covers the resolutions Optima's built-in
    /// profiles use; the driver replicates every global refresh rate across every resolution.
    /// </summary>
    internal const string DefaultSettingsXml = """
        <?xml version='1.0' encoding='utf-8'?>
        <vdd_settings>
            <monitors>
                <count>1</count>
            </monitors>
            <gpu>
                <friendlyname>default</friendlyname>
            </gpu>
            <global>
                <g_refresh_rate>60</g_refresh_rate>
                <g_refresh_rate>90</g_refresh_rate>
                <g_refresh_rate>120</g_refresh_rate>
                <g_refresh_rate>144</g_refresh_rate>
                <g_refresh_rate>165</g_refresh_rate>
                <g_refresh_rate>240</g_refresh_rate>
            </global>
            <resolutions>
                <resolution><width>1280</width><height>720</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>2560</width><height>1440</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>3840</width><height>2160</height><refresh_rate>60</refresh_rate></resolution>
            </resolutions>
            <options>
                <CustomEdid>false</CustomEdid>
                <PreventSpoof>false</PreventSpoof>
                <EdidCeaOverride>false</EdidCeaOverride>
                <HardwareCursor>true</HardwareCursor>
                <SDR10bit>false</SDR10bit>
                <HDRPlus>false</HDRPlus>
                <logging>false</logging>
                <debuglogging>false</debuglogging>
            </options>
        </vdd_settings>
        """;
}
