using System.Runtime.InteropServices;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;
using static Optima.Platform.Windows.NativeMethods.DisplayNative;

namespace Optima.Platform.Windows.Services;

/// <summary>Display control via documented APIs.</summary>
public sealed class WindowsDisplayService : IDisplayService
{
    private readonly ILogger<WindowsDisplayService> _logger;

    public WindowsDisplayService(ILogger<WindowsDisplayService> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DisplayInfo>>(() =>
        {
            var displays = new List<DisplayInfo>();
            for (uint i = 0; ; i++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref adapter, 0))
                {
                    break;
                }
                if ((adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                {
                    continue;
                }

                var attached = (adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                var mode = DEVMODE.Create();
                var hasMode = attached && EnumDisplaySettingsEx(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0);

                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                var hasMonitor = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME);

                displays.Add(new DisplayInfo
                {
                    DeviceName = adapter.DeviceName,
                    FriendlyName = hasMonitor && !string.IsNullOrWhiteSpace(monitor.DeviceString)
                        ? monitor.DeviceString
                        : adapter.DeviceString,
                    AdapterName = adapter.DeviceString,
                    DevicePath = hasMonitor ? monitor.DeviceID : adapter.DeviceID,
                    IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    IsActive = attached,
                    HasMonitor = hasMonitor,
                    CurrentMode = hasMode
                        ? new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency)
                        : default,
                });
            }
            return displays;
        }, ct);

    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(string deviceName, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<DisplayMode>>(() =>
        {
            var modes = new HashSet<DisplayMode>();
            for (var i = 0; ; i++)
            {
                var devMode = DEVMODE.Create();
                if (!EnumDisplaySettingsEx(deviceName, i, ref devMode, 0))
                {
                    break;
                }
                modes.Add(new DisplayMode((int)devMode.dmPelsWidth, (int)devMode.dmPelsHeight, (int)devMode.dmDisplayFrequency));
            }
            return modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.RefreshRate).ToList();
        }, ct);

    public Task ApplyModeAsync(string deviceName, DisplayMode mode, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var devMode = DEVMODE.Create();
            devMode.dmPelsWidth = (uint)mode.Width;
            devMode.dmPelsHeight = (uint)mode.Height;
            devMode.dmDisplayFrequency = (uint)mode.RefreshRate;
            devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            var test = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL)
            {
                throw OptimaException.From(
                    "DISPLAY_MODE_UNSUPPORTED",
                    $"{mode} is not supported on {deviceName}.",
                    "Windows rejected the requested resolution or refresh rate for this display.",
                    null,
                    "Pick one of the resolutions offered on the Display page",
                    "If this is the virtual display, add the mode to the driver's settings and reload the driver");
            }

            // dwFlags = 0 → dynamic, non-persistent change (never CDS_UPDATEREGISTRY).
            var result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != DISP_CHANGE_SUCCESSFUL)
            {
                throw OptimaException.From(
                    "DISPLAY_ACCESS_DENIED",
                    "Unable to change the display configuration.",
                    $"Windows returned code {result} while applying {mode} to {deviceName}.",
                    null,
                    "Close other display-control utilities and try again",
                    "Reinstall the virtual display driver",
                    "Check the display adapter in Device Manager");
            }

            _logger.LogInformation("Resolution applied: {Mode} on {Device}", mode, deviceName);
        }, ct);

    /// <summary>
    /// Makes one display the main screen for now: the one whose desktop starts at 0,0, which is
    /// where Windows opens a program that has no place of its own yet, the game included.
    ///
    /// Done on the display-configuration API this class already restores a layout with. Every
    /// active desktop is moved by the same amount, so the displays keep their places relative to
    /// each other and the target lands on 0,0. Applied without saving it to Windows' own database:
    /// it lasts until the session puts the captured layout back, or until the next sign-in.
    /// </summary>
    public Task MakePrimaryAsync(string deviceName, CancellationToken ct = default)
        => Task.Run(() => MovePrimary(deviceName), ct);

    private void MovePrimary(string deviceName)
    {
        var (paths, modes) = QueryActiveTopology();
        var target = SourceModeIndex(paths, modes, deviceName);
        if (target < 0)
        {
            throw OptimaException.From("DISPLAY_NOT_ACTIVE",
                "That display is not active.",
                $"{deviceName} is not part of the desktop right now, so it cannot become the main screen.");
        }

        var offset = modes[target].sourceMode.position;
        if (offset.x == 0 && offset.y == 0)
        {
            return;
        }
        for (var i = 0; i < modes.Length; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                modes[i].sourceMode.position.x -= offset.x;
                modes[i].sourceMode.position.y -= offset.y;
            }
        }

        var result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
        if (result != 0)
        {
            throw OptimaException.From("DISPLAY_PRIMARY_FAILED",
                "Windows refused to change the primary display.",
                $"SetDisplayConfig returned {result} while moving {deviceName} to 0,0.",
                new System.ComponentModel.Win32Exception(result));
        }

        // Asked again instead of taken on trust: Windows may accept a layout and adjust it.
        var (pathsAfter, modesAfter) = QueryActiveTopology();
        var after = SourceModeIndex(pathsAfter, modesAfter, deviceName);
        if (after < 0 || modesAfter[after].sourceMode.position.x != 0 || modesAfter[after].sourceMode.position.y != 0)
        {
            throw OptimaException.From("DISPLAY_PRIMARY_FAILED",
                "Windows refused to change the primary display.",
                $"The new layout was accepted, but {deviceName} is not at 0,0 afterwards.");
        }
        _logger.LogInformation("{Device} is now the primary display", deviceName);
    }

    /// <summary>
    /// Puts a present-but-detached display on the desktop, to the right of everything already there.
    ///
    /// Done with ChangeDisplaySettingsEx on that one device rather than SetDisplayConfig: it touches
    /// no other display, so the main screen and every other position stay exactly as they are, and
    /// with dwFlags 0 the change is dynamic only (no CDS_UPDATEREGISTRY, no CDS_SET_PRIMARY). Attaching
    /// through SetDisplayConfig would mean rebuilding the whole path set from QDC_ALL_PATHS, and its
    /// topology shortcuts read from and write to Windows' saved layouts.
    ///
    /// The position is never 0,0 while another display is attached, so Windows has no reason to make it
    /// the main screen. If it does anyway, the old main screen is moved back to 0,0 first (GDI refuses to
    /// detach the primary device) and only then is the display detached again, and false returned,
    /// because the virtual display must never become the main screen.
    ///
    /// Only a source with a monitor child counts: Windows lists one GDI source per output an adapter
    /// declares, including outputs where the driver has not (yet) created a monitor, and attaching such
    /// a source does nothing.
    /// </summary>
    public Task<bool> AttachToDesktopAsync(string adapterNameContains, CancellationToken ct = default)
        => Task.Run(() =>
        {
            string? target = null;
            string? oldPrimary = null;
            var rightEdge = 0;
            var anyAttached = false;
            for (uint i = 0; ; i++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref adapter, 0))
                {
                    break;
                }
                if ((adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                {
                    continue;
                }
                var matches = adapter.DeviceString.Contains(adapterNameContains, StringComparison.OrdinalIgnoreCase);
                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    target ??= matches && HasMonitor(adapter.DeviceName) ? adapter.DeviceName : null;
                    continue;
                }
                if (matches)
                {
                    return true;
                }
                anyAttached = true;
                if ((adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                {
                    oldPrimary = adapter.DeviceName;
                }
                var current = DEVMODE.Create();
                if (EnumDisplaySettingsEx(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref current, 0))
                {
                    rightEdge = Math.Max(rightEdge, current.dmPositionX + (int)current.dmPelsWidth);
                }
            }
            if (target is null)
            {
                _logger.LogInformation("No detached display with a monitor on an adapter matching '{Adapter}'", adapterNameContains);
                return false;
            }
            if (!anyAttached)
            {
                // Nothing to extend from: attaching would make it the main screen by definition.
                _logger.LogWarning("{Device} not attached: no other display is on the desktop", target);
                return false;
            }

            var devMode = AttachMode(target);
            devMode.dmPositionX = rightEdge;
            devMode.dmPositionY = 0;
            devMode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            // dwFlags = 0 → dynamic, non-persistent change (never CDS_UPDATEREGISTRY, never CDS_SET_PRIMARY).
            var result = ChangeDisplaySettingsEx(target, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);

            var (attached, primary) = DeviceState(target);
            if (primary)
            {
                UndoPrimary(target, oldPrimary);
                return false;
            }
            _logger.LogInformation("Attach {Device} at {X},0 as {Width}x{Height}: code {Result}, attached {Attached}",
                target, rightEdge, devMode.dmPelsWidth, devMode.dmPelsHeight, result, attached);
            return attached;
        }, ct);

    /// <summary>
    /// Takes the main screen back from a display that was just attached and detaches it. The old main
    /// screen goes back to 0,0 first through the same unsaved SetDisplayConfig move MakePrimaryAsync
    /// uses, because ChangeDisplaySettingsEx refuses to detach the primary device. Every step is
    /// checked, and an error is logged if the display is still the main screen at the end.
    /// </summary>
    private void UndoPrimary(string device, string? oldPrimary)
    {
        if (oldPrimary is not null)
        {
            try
            {
                MovePrimary(oldPrimary);
            }
            catch (Exception ex)
            {
                // Any failure, not only Optima's own: QueryDisplayConfig throws Win32Exception or
                // InvalidOperationException while the layout settles, and the detach below must still run.
                _logger.LogError(ex, "{Device} came up as the main screen and {Old} could not be made the main screen again", device, oldPrimary);
            }
        }
        var off = DEVMODE.Create();
        off.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        var detach = ChangeDisplaySettingsEx(device, ref off, IntPtr.Zero, 0, IntPtr.Zero);
        var (stillAttached, stillPrimary) = DeviceState(device);
        if (stillPrimary)
        {
            _logger.LogError("{Device} came up as the main screen and is STILL the main screen (detach code {Result})", device, detach);
        }
        else
        {
            _logger.LogWarning("{Device} came up as the main screen; {Old} is the main screen again, detach code {Result}, attached {Attached}",
                device, oldPrimary, detach, stillAttached);
        }
    }

    /// <summary>Whether a GDI source has a monitor child, which is what makes it a display rather than an empty output.</summary>
    private static bool HasMonitor(string deviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(deviceName, 0, ref monitor, 0);
    }

    /// <summary>The size to attach a display with: its last one if Windows remembers it, else 1080p, else the first it offers.</summary>
    private static DEVMODE AttachMode(string deviceName)
    {
        var mode = DEVMODE.Create();
        if (EnumDisplaySettingsEx(deviceName, ENUM_REGISTRY_SETTINGS, ref mode, 0) && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
        {
            return mode;
        }
        DEVMODE? first = null;
        for (var i = 0; ; i++)
        {
            var candidate = DEVMODE.Create();
            if (!EnumDisplaySettingsEx(deviceName, i, ref candidate, 0))
            {
                break;
            }
            if (candidate.dmPelsWidth == 1920 && candidate.dmPelsHeight == 1080)
            {
                return candidate;
            }
            first ??= candidate;
        }
        return first ?? DEVMODE.Create();
    }

    /// <summary>Whether a GDI device is on the desktop and whether it is the main screen, as Windows reports it now.</summary>
    private static (bool Attached, bool Primary) DeviceState(string deviceName)
    {
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0))
            {
                return (false, false);
            }
            if (string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0,
                        (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0);
            }
        }
    }

    /// <summary>Where in <paramref name="modes"/> the desktop of a GDI device ("\\.\DISPLAY3") is described, or -1.</summary>
    private static int SourceModeIndex(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, string deviceName)
    {
        foreach (var path in paths)
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0
                || !string.Equals(source.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var index = path.sourceInfo.modeInfoIdx;
            return index < modes.Length && modes[index].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ? (int)index : -1;
        }
        return -1;
    }

    public Task<string> CaptureTopologyAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            var (paths, modes) = QueryActiveTopology();
            var pathBytes = MemoryMarshal.AsBytes(paths.AsSpan()).ToArray();
            var modeBytes = MemoryMarshal.AsBytes(modes.AsSpan()).ToArray();
            var blob = $"v1:{paths.Length}:{Convert.ToBase64String(pathBytes)}:{Convert.ToBase64String(modeBytes)}";
            _logger.LogDebug("Display topology captured ({Paths} paths, {Modes} modes)", paths.Length, modes.Length);
            return blob;
        }, ct);

    public Task RestoreTopologyAsync(string topology, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var parts = topology.Split(':', 4);
            if (parts.Length != 4 || parts[0] != "v1")
            {
                throw new InvalidDataException("Unrecognized topology snapshot format.");
            }

            var pathBytes = Convert.FromBase64String(parts[2]);
            var modeBytes = Convert.FromBase64String(parts[3]);
            var paths = MemoryMarshal.Cast<byte, DISPLAYCONFIG_PATH_INFO>(pathBytes).ToArray();
            var modes = MemoryMarshal.Cast<byte, DISPLAYCONFIG_MODE_INFO>(modeBytes).ToArray();

            var result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG);
            if (result != 0)
            {
                _logger.LogDebug("Exact topology restore returned {Code}; retrying with SDC_ALLOW_CHANGES", result);
                result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            }
            if (result != 0)
            {
                throw OptimaException.From(
                    "DISPLAY_RESTORE_FAILED",
                    "Unable to restore the previous display layout.",
                    $"SetDisplayConfig returned {result}. The saved layout may reference a display that is no longer connected.",
                    new System.ComponentModel.Win32Exception(result),
                    "Reconnect any display that was attached when the session started",
                    "Use Windows Settings > Display to rearrange displays manually");
            }
            _logger.LogInformation("Display topology restored");
        }, ct);

    public Task<IReadOnlyList<(string GdiDevice, string MonitorName, string MonitorPath)>> GetActivePathNamesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<(string, string, string)>>(() =>
        {
            var (paths, _) = QueryActiveTopology();
            var result = new List<(string, string, string)>();
            foreach (var path in paths)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };

                var gdi = DisplayConfigGetDeviceInfo(ref source) == 0 ? source.viewGdiDeviceName : string.Empty;
                var friendly = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.monitorFriendlyDeviceName : string.Empty;
                var devicePath = target.monitorDevicePath ?? string.Empty;
                result.Add((gdi, friendly, devicePath));
            }
            return result;
        }, ct);

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryActiveTopology()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sizeResult = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
            if (sizeResult != 0)
            {
                throw new System.ComponentModel.Win32Exception(sizeResult, "GetDisplayConfigBufferSizes failed");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            var queryResult = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (queryResult == 0)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return (paths, modes);
            }
            const int ERROR_INSUFFICIENT_BUFFER = 122;
            if (queryResult != ERROR_INSUFFICIENT_BUFFER)
            {
                throw new System.ComponentModel.Win32Exception(queryResult, "QueryDisplayConfig failed");
            }
        }
        throw new InvalidOperationException("Display configuration kept changing while being captured.");
    }
}
