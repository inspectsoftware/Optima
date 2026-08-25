using System.Runtime.InteropServices;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;
using static Optima.Platform.Windows.NativeMethods.DisplayNative;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Display control via documented APIs. Mode changes use ChangeDisplaySettingsEx WITHOUT
/// CDS_UPDATEREGISTRY so nothing persists; whole-desktop layout is snapshot/restored through
/// the CCD API (QueryDisplayConfig / SetDisplayConfig) so the user can never be left stranded (§7).
/// </summary>
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
                    "Pick a mode from the supported list on the Display page",
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

    public Task MakePrimaryAsync(string deviceName, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Documented pattern: shift every active display so the target lands at (0,0),
            // batch with CDS_NORESET, then commit with a null final call.
            var targetMode = DEVMODE.Create();
            if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref targetMode, 0))
            {
                throw OptimaException.From("DISPLAY_NOT_ACTIVE",
                    "That display is not active.",
                    $"{deviceName} has no current mode, so it cannot become the primary display.");
            }
            var offsetX = targetMode.dmPositionX;
            var offsetY = targetMode.dmPositionY;
            if (offsetX == 0 && offsetY == 0)
            {
                return; // already primary
            }

            const int CDS_NORESET = 0x10000000;
            const int CDS_SET_PRIMARY = 0x00000010;
            const uint DM_POSITION = 0x20;

            for (uint i = 0; ; i++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref adapter, 0))
                {
                    break;
                }
                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    continue;
                }

                var mode = DEVMODE.Create();
                if (!EnumDisplaySettingsEx(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                {
                    continue;
                }
                mode.dmPositionX -= offsetX;
                mode.dmPositionY -= offsetY;
                mode.dmFields = DM_POSITION;
                var flags = (uint)(CDS_NORESET | (adapter.DeviceName == deviceName ? CDS_SET_PRIMARY : 0));
                _ = ChangeDisplaySettingsEx(adapter.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            }

            var commit = ChangeDisplaySettingsExCommit(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (commit != DISP_CHANGE_SUCCESSFUL)
            {
                throw OptimaException.From("DISPLAY_PRIMARY_FAILED",
                    "Windows refused to change the primary display.",
                    $"ChangeDisplaySettingsEx returned {commit}.");
            }
            _logger.LogInformation("{Device} is now the primary display", deviceName);
        }, ct);

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

            // Exact restore first, because SDC_ALLOW_CHANGES lets Windows remap unusual refresh rates
            // (e.g. a virtual display idling at 999 Hz), so it is only the fallback.
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

    /// <summary>GDI device name (\\.\DISPLAYn) for each active CCD path, with monitor friendly names.</summary>
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
        // Buffer sizes can change between the two calls (hotplug), so retry a few times.
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
