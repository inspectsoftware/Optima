using System.Runtime.InteropServices;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Detection;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Driver.Providers;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.Logging;

namespace Optima.Driver;

/// <summary>Installs the virtual display driver that ships alongside the application, so an end user never opens Device Manager or runs devcon.</summary>
public sealed class VddDriverInstaller : IDriverInstaller
{
    public const string BundledDriverFolder = "drivers";

    private readonly IElevationBroker _elevation;
    private readonly PnpDeviceLocator _deviceLocator;
    private readonly SettingsService _settings;
    private readonly ILogger<VddDriverInstaller> _logger;
    private string? _lastLoggedInfPath;

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
        // The same selection runs inside the elevated helper's one-shot install mode, so the
        // package the setup installs is always the package this app would have installed.
        var folder = Path.Combine(AppContext.BaseDirectory, BundledDriverFolder);
        var package = BundledDriverPackage.Find(folder, note => _logger.LogDebug("{Note}", note));

        if (package is not null && Interlocked.Exchange(ref _lastLoggedInfPath, package.InfPath) != package.InfPath)
        {
            _logger.LogInformation("Bundled driver selected: {Name} {HardwareId} for {Arch} ({Inf})",
                package.DisplayName, package.HardwareId, RuntimeInformation.OSArchitecture, package.InfPath);
        }
        return package;
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
                    "Or install a virtual display driver yourself and Optima will detect and use it",
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
                    "Confirm the bundled driver package is digitally signed, since Windows refuses unsigned driver packages",
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
            Args =
            {
                ["hardwareId"] = package.HardwareId,
                ["infName"] = Path.GetFileName(package.InfPath),
            },
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

        _logger.LogInformation("Virtual display driver removed ({Count} device(s), {Packages} driver package(s))",
            response.Data.GetValueOrDefault("removed"), response.Data.GetValueOrDefault("packagesDeleted", "0"));
        return DriverInstallResult.Ok();
    }

    private async Task EnsureSettingsFileAsync(CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        var path = string.IsNullOrWhiteSpace(settings.VddSettingsPath)
            ? VddSettingsDefaults.DefaultPath
            : settings.VddSettingsPath;

        if (File.Exists(path))
        {
            return;
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.EnsureVddSettings,
            Args = { ["path"] = path, ["content"] = VddSettingsDefaults.DefaultXml },
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
}
