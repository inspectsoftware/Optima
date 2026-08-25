using System.IO.Pipes;
using System.Text;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Configuration;
using COPSBootstrapper.Core.Ipc;
using COPSBootstrapper.Core.Models;
using COPSBootstrapper.Platform.Windows.Services;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.Driver.Providers;

/// <summary>
/// Provider for the MikeTheTech-style IddCx "Virtual Display Driver":
///  - modes are declared in vdd_settings.xml (edited non-destructively, original backed up first),
///  - the driver re-reads settings when RELOAD_DRIVER is written to \\.\pipe\MTTVirtualDisplayPipe,
///  - the display device itself is enabled/disabled through the elevated helper (pnputil),
///  - the Windows-side mode switch goes through IDisplayService (temporary, registry untouched).
/// Everything done here is recorded and reverted by RestoreOriginalStateAsync.
/// </summary>
public sealed class MttVddProvider : VirtualDisplayProviderBase
{
    public const string DefaultSettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    public const string PipeName = "MTTVirtualDisplayPipe";
    public const string DeviceNameMarker = "Virtual Display Driver";
    private const string ReloadCommand = "RELOAD_DRIVER";

    private readonly IDisplayService _displayService;
    private readonly IElevationBroker _elevation;
    private readonly PnpDeviceLocator _deviceLocator;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<MttVddProvider> _logger;

    private string? _originalSettingsXml;
    private string? _backupPath;
    private bool _settingsChanged;
    private bool _deviceWasEnabledInitially = true;
    private bool _displayWasActiveInitially;
    private bool _weEnabledDevice;

    public MttVddProvider(
        IDisplayService displayService,
        IElevationBroker elevation,
        PnpDeviceLocator deviceLocator,
        SettingsService settings,
        AppPaths paths,
        ILogger<MttVddProvider> logger)
    {
        _displayService = displayService;
        _elevation = elevation;
        _deviceLocator = deviceLocator;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public override string Name => "Virtual Display Driver (MikeTheTech)";

    /// <summary>Path of the backup taken before we first rewrote the driver settings, if any.</summary>
    public string? SettingsBackupPath => _settingsChanged ? _backupPath : null;

    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (File.Exists(await GetSettingsPathAsync(ct).ConfigureAwait(false)))
        {
            return true;
        }
        if (PipeExists())
        {
            return true;
        }
        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        return devices.Count > 0;
    }

    public override Task<DriverCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(new DriverCapabilities
        {
            SupportsCustomModes = true,          // any mode can be added to vdd_settings.xml
            SupportsRefreshRateChange = true,
            SupportsGpuPinning = true,           // <gpu><friendlyname> in the settings file
            SupportsEnableDisable = true,
            RequiresElevation = true,            // device toggle + settings file under C:\
        });

    private string MarkerPath => Path.Combine(_paths.BackupsDirectory, "vdd-settings.pending");

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        // A leftover marker means a previous session modified the driver settings and crashed
        // before restoring them — put the original file back before doing anything else.
        await RestoreSettingsFromMarkerAsync(ct).ConfigureAwait(false);

        var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);
        if (File.Exists(settingsPath) && _originalSettingsXml is null)
        {
            _originalSettingsXml = await File.ReadAllTextAsync(settingsPath, ct).ConfigureAwait(false);
            Directory.CreateDirectory(_paths.BackupsDirectory);
            _backupPath = Path.Combine(_paths.BackupsDirectory, $"vdd_settings-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.xml");
            await File.WriteAllTextAsync(_backupPath, _originalSettingsXml, ct).ConfigureAwait(false);
        }

        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        _deviceWasEnabledInitially = devices.Count == 0 || devices.Any(d => d.Enabled);
        _displayWasActiveInitially = await IsDisplayActiveAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Virtual display driver initialized (device enabled: {Enabled}, display active: {Active}, settings: {Path})",
            _deviceWasEnabledInitially, _displayWasActiveInitially, settingsPath);
    }

    public override Task CreateDisplayAsync(CancellationToken ct = default) => EnableDisplayAsync(ct);

    public override async Task EnableDisplayAsync(CancellationToken ct = default)
    {
        if (await IsDisplayActiveAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            throw CopsException.From("VDD_NOT_INSTALLED",
                "No virtual display driver device was found.",
                "The Virtual Display Driver does not appear in Device Manager.",
                null,
                "Install the Virtual Display Driver",
                "Check Device Manager under Display adapters");
        }

        var disabled = devices.FirstOrDefault(d => !d.Enabled);
        if (disabled is not null)
        {
            await SendDeviceCommandAsync(IpcCommand.EnableDevice, disabled.InstanceId, ct).ConfigureAwait(false);
            _weEnabledDevice = true;
        }

        // The device is enabled but the monitor can take a moment to attach to the desktop.
        if (!await WaitForDisplayStateAsync(active: true, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            throw CopsException.From("VDD_NO_DISPLAY",
                "The virtual display did not appear.",
                "The driver device is enabled but Windows never attached its display to the desktop.",
                null,
                "Send RELOAD_DRIVER from the Display page",
                "Check vdd_settings.xml has a monitor count of at least 1",
                "Reinstall the virtual display driver");
        }
        _logger.LogInformation("Virtual display created and attached to the desktop");
    }

    public override async Task DisableDisplayAsync(CancellationToken ct = default)
    {
        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        var enabled = devices.FirstOrDefault(d => d.Enabled);
        if (enabled is null)
        {
            return;
        }
        await SendDeviceCommandAsync(IpcCommand.DisableDevice, enabled.InstanceId, ct).ConfigureAwait(false);
        await WaitForDisplayStateAsync(active: false, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _logger.LogInformation("Virtual display disabled");
    }

    public override async Task SetModeAsync(DisplayMode mode, CancellationToken ct = default)
    {
        var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);

        // 1. Make sure the driver advertises the mode; reload it when the settings change.
        //    When the settings file cannot be rewritten (it usually needs admin), fail safely
        //    by snapping to the closest mode the driver already advertises (§7).
        if (File.Exists(settingsPath))
        {
            var document = VddSettingsDocument.Load(settingsPath);
            var advertised = document.GetAdvertisedModes();
            if (!advertised.Contains(mode))
            {
                try
                {
                    document.EnsureMode(mode);
                    if (_backupPath is not null)
                    {
                        // Persist the pending-change marker BEFORE the edit so even a crash right
                        // after the write is recoverable on the next start (§18).
                        await File.WriteAllLinesAsync(MarkerPath, [_backupPath, settingsPath], ct).ConfigureAwait(false);
                    }
                    document.Save(settingsPath);
                    _settingsChanged = true;
                    _logger.LogInformation("Added {Mode} to vdd_settings.xml — reloading driver", mode);
                    await ReloadDriverAsync(ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    var closest = ClosestAdvertisedMode(advertised, mode);
                    if (closest is null)
                    {
                        throw CopsException.From("VDD_SETTINGS_LOCKED",
                            "The driver settings file could not be updated.",
                            $"Writing {settingsPath} requires administrator access and no similar mode is available.",
                            ex,
                            "Run the app once as administrator to add the mode",
                            $"Or add {mode.Width}x{mode.Height} @ {mode.RefreshRate} Hz to the settings file manually");
                    }
                    _logger.LogWarning(ex,
                        "Cannot update vdd_settings.xml without elevation — using closest advertised mode {Closest} instead of {Requested}",
                        closest, mode);
                    mode = closest.Value;
                }
            }
        }

        // 2. Apply the mode on the Windows side (temporary; never persisted to the registry).
        var display = await GetDisplayInfoAsync(ct).ConfigureAwait(false)
            ?? throw CopsException.From("VDD_NO_DISPLAY",
                "The virtual display is not active.",
                "A display mode can only be applied while the virtual display is attached.",
                null,
                "Enable the virtual display first");

        await _displayService.ApplyModeAsync(display.DeviceName, mode, ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
    {
        // Prefer the Windows-reported mode list of the live display; fall back to the settings file.
        var display = await GetDisplayInfoAsync(ct).ConfigureAwait(false);
        if (display is not null)
        {
            var windowsModes = await _displayService.GetSupportedModesAsync(display.DeviceName, ct).ConfigureAwait(false);
            if (windowsModes.Count > 0)
            {
                return windowsModes;
            }
        }

        var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);
        return File.Exists(settingsPath)
            ? VddSettingsDocument.Load(settingsPath).GetAdvertisedModes()
            : [];
    }

    public override async Task<DisplayMode?> GetCurrentModeAsync(CancellationToken ct = default)
        => (await GetDisplayInfoAsync(ct).ConfigureAwait(false))?.CurrentMode;

    public override async Task<bool> IsDisplayActiveAsync(CancellationToken ct = default)
        => await GetDisplayInfoAsync(ct).ConfigureAwait(false) is not null;

    public override async Task<DisplayInfo?> GetDisplayInfoAsync(CancellationToken ct = default)
    {
        var displays = await _displayService.GetDisplaysAsync(ct).ConfigureAwait(false);
        return displays.FirstOrDefault(d =>
            d.IsActive && d.AdapterName.Contains(DeviceNameMarker, StringComparison.OrdinalIgnoreCase));
    }

    public override async Task RestoreOriginalStateAsync(CancellationToken ct = default)
    {
        // Restore driver settings first so a reload advertises the original modes again.
        if (_settingsChanged && _originalSettingsXml is not null)
        {
            var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);
            try
            {
                await File.WriteAllTextAsync(settingsPath, _originalSettingsXml, ct).ConfigureAwait(false);
                _settingsChanged = false;
                TryDeleteMarker();
                await ReloadDriverAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("vdd_settings.xml restored from backup");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not restore vdd_settings.xml — backup remains at {Backup}", _backupPath);
            }
        }
        else
        {
            // Fresh process (crash recovery): fall back to the on-disk pending marker.
            await RestoreSettingsFromMarkerAsync(ct).ConfigureAwait(false);
        }

        if (_weEnabledDevice && !_deviceWasEnabledInitially)
        {
            await DisableDisplayAsync(ct).ConfigureAwait(false);
            _weEnabledDevice = false;
        }
    }

    /// <summary>Restores the settings file recorded in the crash marker, then reloads the driver.</summary>
    private async Task RestoreSettingsFromMarkerAsync(CancellationToken ct)
    {
        if (!File.Exists(MarkerPath))
        {
            return;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(MarkerPath, ct).ConfigureAwait(false);
            if (lines.Length >= 2 && File.Exists(lines[0]))
            {
                File.Copy(lines[0], lines[1], overwrite: true);
                _logger.LogInformation("vdd_settings.xml restored from crash-recovery backup {Backup}", lines[0]);
                TryDeleteMarker();
                await ReloadDriverAsync(ct).ConfigureAwait(false);
            }
            else
            {
                TryDeleteMarker();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Crash-recovery restore of vdd_settings.xml failed — marker kept for the next attempt");
        }
    }

    private void TryDeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes RELOAD_DRIVER to the driver's control pipe (directly, or elevated as fallback).</summary>
    public async Task ReloadDriverAsync(CancellationToken ct = default)
    {
        try
        {
            await WritePipeDirectAsync(ReloadCommand, ct).ConfigureAwait(false);
            _logger.LogInformation("RELOAD_DRIVER sent to the virtual display driver");
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or TimeoutException)
        {
            _logger.LogWarning(ex, "Direct pipe write failed — retrying through the elevated helper");
        }

        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw CopsException.From("VDD_PIPE_DENIED",
                "Could not signal the virtual display driver.",
                "Writing to the driver control pipe requires administrator access, and the elevated helper is not available.",
                null,
                "Approve the administrator prompt when it appears",
                "Check that the Virtual Display Driver service is running");
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.WriteVddPipe,
            Args = { ["pipeName"] = PipeName, ["command"] = ReloadCommand },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            throw CopsException.From("VDD_PIPE_FAILED",
                "The virtual display driver did not accept the reload request.",
                response.Error);
        }
    }

    /// <summary>Same resolution at the nearest refresh rate, else the overall nearest mode.</summary>
    internal static DisplayMode? ClosestAdvertisedMode(IReadOnlyList<DisplayMode> advertised, DisplayMode requested)
    {
        if (advertised.Count == 0)
        {
            return null;
        }
        var sameResolution = advertised
            .Where(m => m.Width == requested.Width && m.Height == requested.Height)
            .OrderBy(m => Math.Abs(m.RefreshRate - requested.RefreshRate))
            .ToList();
        if (sameResolution.Count > 0)
        {
            return sameResolution[0];
        }
        return advertised
            .OrderBy(m => Math.Abs((long)m.Width * m.Height - (long)requested.Width * requested.Height))
            .ThenBy(m => Math.Abs(m.RefreshRate - requested.RefreshRate))
            .First();
    }

    private static bool PipeExists()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(PipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WritePipeDirectAsync(string command, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        // The driver reads wide-character text; send UTF-16LE with a terminating null.
        var bytes = Encoding.Unicode.GetBytes(command + "\0");
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendDeviceCommandAsync(IpcCommand command, string instanceId, CancellationToken ct)
    {
        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw CopsException.From("ELEVATION_DECLINED",
                "Administrator access is needed to switch the virtual display.",
                "Enabling or disabling the virtual display device requires the elevated helper.",
                null,
                "Approve the administrator prompt when launching",
                "Or enable the Virtual Display Driver manually in Device Manager");
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = command,
            Args = { ["instanceId"] = instanceId },
        }, ct).ConfigureAwait(false);

        if (!response.Success)
        {
            throw CopsException.From("DEVICE_TOGGLE_FAILED",
                "Windows refused to change the virtual display device.",
                response.Error,
                null,
                "Check the device in Device Manager under Display adapters",
                "Reinstall the virtual display driver");
        }
    }

    private async Task<bool> WaitForDisplayStateAsync(bool active, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsDisplayActiveAsync(ct).ConfigureAwait(false) == active)
            {
                return true;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<string> GetSettingsPathAsync(CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(settings.VddSettingsPath) ? DefaultSettingsPath : settings.VddSettingsPath;
    }
}
