using System.IO.Pipes;
using System.Text;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Ipc;
using Optima.Core.Models;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.Logging;

namespace Optima.Driver.Providers;

/// <summary>
/// Provider for the MikeTheTech-style IddCx "Virtual Display Driver": - modes are declared in vdd_settings.xml (edited
/// non-destructively, original backed up first), - the driver re-reads settings when RELOAD_DRIVER is written to
/// \\.\pipe\MTTVirtualDisplayPipe, - the display device itself is enabled/disabled through the elevated helper
/// (pnputil), - the Windows-side mode switch goes through IDisplayService (temporary, registry untouched).
/// </summary>
public sealed class MttVddProvider : VirtualDisplayProviderBase, IVirtualDisplayMaintenance
{
    public const string DefaultSettingsPath = VddSettingsDefaults.DefaultPath;
    public const string PipeName = "MTTVirtualDisplayPipe";
    public const string DeviceNameMarker = "Virtual Display Driver";
    private const string ReloadCommand = "RELOAD_DRIVER";

    // How often the wait loops look again. A look is one display enumeration, about a millisecond,
    // and the display attaches a few hundred milliseconds after the device is enabled or the driver
    // reloads, so a coarser step is time added to every virtual display launch.
    private const int WaitPollMs = 100;

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
    private string? _enabledInstanceId;

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

    public override string Name => "Virtual display driver";

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
            SupportsCustomModes = true,
            SupportsRefreshRateChange = true,
            SupportsGpuPinning = true,
            SupportsEnableDisable = true,
            RequiresElevation = true,
        });

    private string MarkerPath => _paths.VddRestoreMarkerFile;

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await RestoreSettingsFromMarkerAsync(ct).ConfigureAwait(false);

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        var (settingsPath, source) = DriverSettingsLocation.Locate(settings.VddSettingsPath);
        _logger.LogInformation("Virtual display settings file: {Path} (from {Source})", settingsPath, source);
        if (!File.Exists(settingsPath))
        {
            await EnsureDefaultSettingsFileAsync(settingsPath, ct).ConfigureAwait(false);
        }

        if (File.Exists(settingsPath) && _originalSettingsXml is null)
        {
            _originalSettingsXml = await File.ReadAllTextAsync(settingsPath, ct).ConfigureAwait(false);
            Directory.CreateDirectory(_paths.BackupsDirectory);
            _backupPath = Path.Combine(_paths.BackupsDirectory, $"vdd_settings-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.xml");
            await File.WriteAllTextAsync(_backupPath, _originalSettingsXml, ct).ConfigureAwait(false);
        }

        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        // The first node is the one EnableDisplayAsync acts on, so it is the one whose state is put back.
        _deviceWasEnabledInitially = devices.Count == 0 || devices[0].Enabled;
        _displayWasActiveInitially = await IsDisplayActiveAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Virtual display driver initialized (device enabled: {Enabled}, display active: {Active}, settings: {Path})",
            _deviceWasEnabledInitially, _displayWasActiveInitially, settingsPath);
    }

    public override bool DeviceWasEnabledInitially => _deviceWasEnabledInitially;

    public override Task CreateDisplayAsync(CancellationToken ct = default) => EnableDisplayAsync(ct);

    /// <summary>
    /// Turns the display on according to what Device Manager says about the device: switched off is
    /// switched on, a start failure gets one restart, and a fault no restart fixes is reported by
    /// its own code at once instead of after a 15-second wait for a display that cannot come.
    /// </summary>
    public override async Task EnableDisplayAsync(CancellationToken ct = default)
    {
        if (await IsDisplayActiveAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var device = await ProbeDeviceAsync(ct).ConfigureAwait(false);
        switch (ActionFor(device.ProblemCode))
        {
            case DeviceAction.Enable:
                await SendDeviceCommandAsync(IpcCommand.EnableDevice, device.InstanceId, ct).ConfigureAwait(false);
                _weEnabledDevice = true;
                _enabledInstanceId = device.InstanceId;
                break;
            case DeviceAction.Restart:
                // A start failure is often a driver that came up before something it needs; starting
                // it again once is what a disable and enable in Device Manager does by hand.
                _logger.LogWarning("Virtual display device {Id} has problem code {Code}; restarting it once",
                    device.InstanceId, device.ProblemCode);
                try
                {
                    await SendDeviceCommandAsync(IpcCommand.RestartDevice, device.InstanceId, ct).ConfigureAwait(false);
                }
                catch (OptimaException ex) when (ex.Error.Code == "DEVICE_TOGGLE_FAILED")
                {
                    // Half a restart: the disable went through and the enable did not. The device must
                    // end switched on, as it started, and the error must name its code rather than
                    // "could not toggle".
                    _logger.LogWarning(ex, "Restarting virtual display device {Id} did not fully succeed; checking its state", device.InstanceId);
                    if ((await ProbeDeviceAsync(ct).ConfigureAwait(false)).ProblemCode == PnpDeviceLocator.ProblemDisabled)
                    {
                        await SendDeviceCommandAsync(IpcCommand.EnableDevice, device.InstanceId, ct).ConfigureAwait(false);
                    }
                }
                var codeBeforeRestart = device.ProblemCode;
                device = await ProbeDeviceAsync(ct).ConfigureAwait(false);
                if (!device.Healthy)
                {
                    throw ProblemError(device.ProblemCode == PnpDeviceLocator.ProblemDisabled ? codeBeforeRestart : device.ProblemCode);
                }
                break;
            case DeviceAction.Fail:
                throw ProblemError(device.ProblemCode);
        }

        if (await WaitForDisplayStateAsync(active: true, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            await ThrowIfMainScreenAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Virtual display created and attached to the desktop");
            return;
        }

        // The device can fail while it starts, and its code names the problem better than "no display".
        device = await ProbeDeviceAsync(ct).ConfigureAwait(false);
        if (!device.Healthy)
        {
            throw ProblemError(device.ProblemCode);
        }

        // Windows keeps a display it once saw detached as detached. Attaching puts it to the right of
        // the desktop, never as the main screen, and only until Windows next changes the layout.
        if (await _displayService.AttachToDesktopAsync(DeviceNameMarker, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("The virtual display was present but off the desktop; attached it");
        }
        if (await WaitForDisplayStateAsync(active: true, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            // Active is not success: an attach that came up as the main screen and could not be
            // taken back is still active, and must fail the session rather than run it there.
            await ThrowIfMainScreenAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Virtual display created and attached to the desktop");
            return;
        }

        // A display that is there but off the desktop is Windows' layout, which a driver reload does
        // not touch; no display at all is the driver offering none, which a reload can fix.
        var displays = await _displayService.GetDisplaysAsync(ct).ConfigureAwait(false);
        // Only a source with a monitor is a display: an empty output on the adapter is the driver
        // offering none, which is VDD_NO_DISPLAY and its reload, not a layout problem.
        var sources = displays.Where(d => d.AdapterName.Contains(DeviceNameMarker, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var source in sources)
        {
            _logger.LogInformation("Virtual display source {Device}: monitor {HasMonitor}, attached {Active}",
                source.DeviceName, source.HasMonitor, source.IsActive);
        }
        if (sources.Any(d => d.HasMonitor))
        {
            throw OptimaException.From("VDD_NOT_ATTACHED",
                "Windows did not add the virtual display to the desktop.",
                "The driver is working and its display is there, but Windows kept it off the desktop and did not accept attaching it.",
                null,
                "In Windows Settings > System > Display, choose Extend desktop to this display for the virtual display",
                "Run Check my setup on the Display page again");
        }
        throw OptimaException.From("VDD_NO_DISPLAY",
            "The virtual display did not appear.",
            "The driver device is working but it offered Windows no display.",
            null,
            "Press Reload driver in Check my setup on the Display page",
            "Check vdd_settings.xml has a monitor count of at least 1",
            "Reinstall the virtual display driver");
    }

    /// <summary>Whether a display of this driver is the main screen, which it must never be.</summary>
    internal static bool IsMainScreen(IEnumerable<DisplayInfo> displays)
        => displays.Any(d => d.IsPrimary && d.AdapterName.Contains(DeviceNameMarker, StringComparison.OrdinalIgnoreCase));

    private async Task ThrowIfMainScreenAsync(CancellationToken ct)
    {
        if (IsMainScreen(await _displayService.GetDisplaysAsync(ct).ConfigureAwait(false)))
        {
            throw OptimaException.From("VDD_BECAME_MAIN_SCREEN",
                "The virtual display became the main screen.",
                "Windows made the virtual display the main screen when it was added, and it could not be moved back.",
                null,
                "In Windows Settings > System > Display, select your own monitor and choose Make this my main display",
                "Run Check my setup on the Display page again");
        }
    }

    /// <summary>What turning the display on does about the device's problem code.</summary>
    internal enum DeviceAction
    {
        None,
        Enable,
        Restart,
        Fail,
    }

    /// <summary>
    /// 0 needs nothing; 22 is switched off and gets switched on; a start failure (10, 31, 43) gets
    /// one restart; everything else (no driver, a pending restart, a blocked signature) is not
    /// helped by a restart and fails at once with <see cref="ProblemError"/>.
    /// </summary>
    internal static DeviceAction ActionFor(int problemCode) => problemCode switch
    {
        0 => DeviceAction.None,
        PnpDeviceLocator.ProblemDisabled => DeviceAction.Enable,
        10 or 31 or 43 => DeviceAction.Restart,
        _ => DeviceAction.Fail,
    };

    /// <summary>Device Manager's codes for a node with no driver bound to it: only a reinstall helps.</summary>
    internal static bool HasNoDriver(int problemCode) => problemCode is 1 or 18 or 28;

    /// <summary>The error for a device that cannot be used as it is, with what to do about its code.</summary>
    internal static OptimaException ProblemError(int problemCode)
    {
        if (HasNoDriver(problemCode))
        {
            return OptimaException.From("VDD_DEVICE_NO_DRIVER",
                "The virtual display device has no driver.",
                $"Windows lists the device but did not bind the driver to it (problem code {problemCode}).",
                null,
                "Reinstall the virtual display driver from the Display page");
        }
        return problemCode switch
        {
            14 => OptimaException.From("VDD_RESTART_NEEDED",
                "Windows needs a restart before the virtual display can work.",
                "The driver was installed or changed, and Windows finishes that only at the next restart (problem code 14).",
                null,
                "Restart the PC, then press PLAY again"),
            52 => OptimaException.From("VDD_DRIVER_BLOCKED",
                "Windows blocked the virtual display driver.",
                "Windows could not verify the driver's digital signature and would not load it (problem code 52).",
                null,
                "Reinstall the virtual display driver from the Display page",
                "Check whether security software is blocking the driver"),
            _ => OptimaException.From("VDD_DEVICE_FAILED",
                $"The virtual display device is not working (problem code {problemCode}).",
                $"Device Manager reports problem code {problemCode} for the virtual display device.",
                null,
                "Reinstall the virtual display driver from the Display page",
                "Look the code up in Device Manager under Display adapters"),
        };
    }

    /// <summary>The healthiest virtual display node (every node is logged by the locator), or VDD_NOT_INSTALLED.</summary>
    private async Task<PnpDeviceLocator.PnpDevice> ProbeDeviceAsync(CancellationToken ct)
    {
        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            throw OptimaException.From("VDD_NOT_INSTALLED",
                "No virtual display driver device was found.",
                "The Virtual Display Driver does not appear in Device Manager.",
                null,
                "Install the Virtual Display Driver",
                "Check Device Manager under Display adapters");
        }
        return devices[0];
    }

    public override async Task DisableDisplayAsync(CancellationToken ct = default)
    {
        var devices = await _deviceLocator.FindDisplayDevicesAsync(DeviceNameMarker, ct).ConfigureAwait(false);
        // A node with no driver is skipped: disabling it only turns its code into 22, which then reads
        // as an installed driver and hides the reinstall that is the only fix.
        var enabled = devices.FirstOrDefault(d => d.Enabled && !HasNoDriver(d.ProblemCode));
        if (enabled is null)
        {
            return;
        }
        await DisableDeviceAsync(enabled.InstanceId, ct).ConfigureAwait(false);
    }

    private async Task DisableDeviceAsync(string instanceId, CancellationToken ct)
    {
        await SendDeviceCommandAsync(IpcCommand.DisableDevice, instanceId, ct).ConfigureAwait(false);
        await WaitForDisplayStateAsync(active: false, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _logger.LogInformation("Virtual display disabled");
    }

    public override async Task SetModeAsync(DisplayMode mode, CancellationToken ct = default)
    {
        var display = await GetDisplayInfoAsync(ct).ConfigureAwait(false)
            ?? throw OptimaException.From("VDD_NO_DISPLAY",
                "The virtual display is not active.",
                "A display mode can only be applied while the virtual display is attached.",
                null,
                "Enable the virtual display first");

        // 1. Make sure the driver actually advertises the mode. The Windows mode list of the
        //    live display is the source of truth here, not vdd_settings.xml: the file can list
        //    a mode the driver never loaded (edited by an earlier session that never reloaded,
        //    or restored behind the driver's back), and trusting it skips the reload, after
        //    which Windows quietly lands on whatever the driver prefers instead (e.g. its
        //    999 Hz placeholder mode).
        var liveModes = await _displayService.GetSupportedModesAsync(display.DeviceName, ct).ConfigureAwait(false);
        if (!liveModes.Contains(mode))
        {
            mode = await MakeDriverAdvertiseModeAsync(mode, ct).ConfigureAwait(false);

            display = await GetDisplayInfoAsync(ct).ConfigureAwait(false)
                ?? throw OptimaException.From("VDD_NO_DISPLAY",
                    "The virtual display did not come back after the driver reload.",
                    "The driver was reloaded to pick up the new mode but its display never re-attached.",
                    null,
                    "Enable the virtual display again from the Display page");
        }

        // 2. Apply the mode on the Windows side (temporary; never persisted to the registry).
        await _displayService.ApplyModeAsync(display.DeviceName, mode, ct).ConfigureAwait(false);

        await VerifyModeAppliedAsync(mode, ct).ConfigureAwait(false);
    }

    private async Task<DisplayMode> MakeDriverAdvertiseModeAsync(DisplayMode mode, CancellationToken ct)
    {
        var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);
        if (!File.Exists(settingsPath))
        {
            return mode;
        }

        var document = VddSettingsDocument.Load(settingsPath);
        // Taken before EnsureMode adds the mode to the document in memory: the fallback below has to
        // choose among what the driver really advertises.
        var advertised = document.GetAdvertisedModes();
        try
        {
            if (document.EnsureMode(mode))
            {
                if (_backupPath is not null)
                {
                    await File.WriteAllLinesAsync(MarkerPath, [_backupPath, settingsPath], ct).ConfigureAwait(false);
                }
                try
                {
                    document.Save(settingsPath);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Nothing was changed, so there is nothing for the marker to restore.
                    TryDeleteMarker();
                    throw;
                }
                _settingsChanged = true;
                _logger.LogInformation("Added {Mode} to vdd_settings.xml, reloading driver", mode);
            }

            await ReloadDriverAsync(ct).ConfigureAwait(false);
            if (!await WaitForAdvertisedModeAsync(mode, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Driver still does not advertise {Mode} after a reload; applying it anyway", mode);
            }
            return mode;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var closest = ClosestAdvertisedMode(advertised, mode);
            if (closest is null)
            {
                throw OptimaException.From("VDD_SETTINGS_LOCKED",
                    "The driver settings file could not be updated.",
                    $"Writing {settingsPath} requires administrator access and no similar mode is available.",
                    ex,
                    "Run the app once as administrator to add the mode",
                    $"Or add {mode.Width}x{mode.Height} @ {mode.RefreshRate} Hz to the settings file manually");
            }
            _logger.LogWarning(ex,
                "Cannot update vdd_settings.xml without elevation, using closest advertised mode {Closest} instead of {Requested}",
                closest, mode);
            return closest.Value;
        }
    }

    private async Task<bool> WaitForAdvertisedModeAsync(DisplayMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } display)
            {
                var modes = await _displayService.GetSupportedModesAsync(display.DeviceName, ct).ConfigureAwait(false);
                if (modes.Contains(mode))
                {
                    return true;
                }
            }
            await Task.Delay(WaitPollMs, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task VerifyModeAppliedAsync(DisplayMode requested, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (IsModeApplied(await GetCurrentModeAsync(ct).ConfigureAwait(false), requested))
                {
                    return;
                }
                await Task.Delay(WaitPollMs, ct).ConfigureAwait(false);
            }

            if (attempt == 0 && await GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } display)
            {
                _logger.LogWarning("Virtual display settled at {Actual} instead of {Requested}; re-applying",
                    display.CurrentMode, requested);
                await _displayService.ApplyModeAsync(display.DeviceName, requested, ct).ConfigureAwait(false);
            }
        }

        var actual = await GetCurrentModeAsync(ct).ConfigureAwait(false);
        throw OptimaException.From("VDD_MODE_NOT_APPLIED",
            $"The virtual display is running at {actual?.ToString() ?? "an unknown mode"} instead of {requested}.",
            "Windows accepted the mode change but the driver reverted to its own preferred mode.",
            null,
            "Apply the mode again",
            "Check the refresh rates listed in vdd_settings.xml, then reload the driver");
    }

    private static bool IsModeApplied(DisplayMode? current, DisplayMode requested)
        => current is { } mode
            && mode.Width == requested.Width
            && mode.Height == requested.Height
            && Math.Abs(mode.RefreshRate - requested.RefreshRate) <= 1;

    public override async Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct = default)
    {
        var display = await GetDisplayInfoAsync(ct).ConfigureAwait(false);
        if (display is not null)
        {
            var windowsModes = await _displayService.GetSupportedModesAsync(display.DeviceName, ct).ConfigureAwait(false);
            var plausible = windowsModes.Where(m => m.IsValid).ToList();
            if (plausible.Count > 0)
            {
                return plausible;
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
        if (_settingsChanged && _originalSettingsXml is not null)
        {
            var settingsPath = await GetSettingsPathAsync(ct).ConfigureAwait(false);
            try
            {
                await File.WriteAllTextAsync(settingsPath, _originalSettingsXml, ct).ConfigureAwait(false);
                _settingsChanged = false;
                TryDeleteMarker();
                _logger.LogInformation("vdd_settings.xml restored from backup");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not restore vdd_settings.xml, backup remains at {Backup}", _backupPath);
            }

            if (!_settingsChanged)
            {
                try
                {
                    await ReloadDriverAsync(ct).ConfigureAwait(false);
                }
                catch (OptimaException ex)
                {
                    // The file is back; a driver that will not reload must not stop the device below
                    // from being switched off again.
                    _logger.LogWarning(ex, "The driver did not reload after vdd_settings.xml was restored");
                }
            }
        }
        else
        {
            await RestoreSettingsFromMarkerAsync(ct).ConfigureAwait(false);
        }

        if (_weEnabledDevice && !_deviceWasEnabledInitially)
        {
            // The node this session switched on, not whichever is on first: with several nodes the
            // first "enabled" one can be another that was on from the start.
            if (_enabledInstanceId is not null)
            {
                await DisableDeviceAsync(_enabledInstanceId, ct).ConfigureAwait(false);
            }
            else
            {
                await DisableDisplayAsync(ct).ConfigureAwait(false);
            }
            _weEnabledDevice = false;
            _enabledInstanceId = null;
        }
    }

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
                try
                {
                    await ReloadDriverAsync(ct).ConfigureAwait(false);
                }
                catch (OptimaException ex)
                {
                    // The file is back, which is what the marker was for. A driver that is not there to
                    // reload reads it when it next loads, and must not fail the launch that got here.
                    _logger.LogWarning(ex, "The driver did not reload after vdd_settings.xml was restored");
                }
            }
            else
            {
                TryDeleteMarker();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Crash-recovery restore of vdd_settings.xml failed, marker kept for the next attempt");
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

    /// <inheritdoc cref="IVirtualDisplayMaintenance.ReloadDriverAsync" />
    public async Task ReloadDriverAsync(CancellationToken ct = default)
    {
        try
        {
            await WritePipeDirectAsync(ReloadCommand, ct).ConfigureAwait(false);
            _logger.LogInformation("RELOAD_DRIVER sent to the virtual display driver");
            return;
        }
        catch (TimeoutException ex)
        {
            // Nobody answered, which is a driver that is not loaded. Access denied is thrown at
            // once, so the helper has nothing to add here: it would wait out the same three seconds
            // behind an administrator prompt and fail the same way.
            throw OptimaException.From("VDD_PIPE_FAILED",
                "The virtual display driver is not running.",
                "Its control pipe did not answer within three seconds, so there was no driver to tell to reload.",
                ex,
                "Enable the virtual display from the Display page, then try again",
                "Reinstall the virtual display driver from the Display page");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Direct pipe write failed, retrying through the elevated helper");
        }

        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw OptimaException.From("VDD_PIPE_DENIED",
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
            throw OptimaException.From("VDD_PIPE_FAILED",
                "The virtual display driver did not accept the reload request.",
                response.Error);
        }
    }

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
        var bytes = Encoding.Unicode.GetBytes(command + "\0");
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendDeviceCommandAsync(IpcCommand command, string instanceId, CancellationToken ct)
    {
        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            throw OptimaException.From("ELEVATION_DECLINED",
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
            throw OptimaException.From("DEVICE_TOGGLE_FAILED",
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
            await Task.Delay(WaitPollMs, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// The driver advertises no modes at all without this file, and it is easy to end up without
    /// one: the installer writes it when it installs the driver, but a package installed by other
    /// means (a manual Device Manager install, or a copy restored from a backup) leaves none. So
    /// the missing file is created here too, through the same elevated helper command the driver
    /// install uses.
    /// </summary>
    private async Task EnsureDefaultSettingsFileAsync(string settingsPath, CancellationToken ct)
    {
        if (!await _elevation.EnsureStartedAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "No driver settings file at {Path} and administrator access was declined, so the driver has no modes yet",
                settingsPath);
            return;
        }

        var response = await _elevation.SendAsync(new IpcRequest
        {
            Command = IpcCommand.EnsureVddSettings,
            Args = { ["path"] = settingsPath, ["content"] = VddSettingsDefaults.DefaultXml },
        }, ct).ConfigureAwait(false);

        if (response.Success)
        {
            _logger.LogInformation("Default driver settings written to {Path}", settingsPath);
        }
        else
        {
            _logger.LogWarning("Could not create the driver settings file at {Path}: {Error}", settingsPath, response.Error);
        }
    }

    private async Task<string> GetSettingsPathAsync(CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        return DriverSettingsLocation.Resolve(settings.VddSettingsPath);
    }
}
