using Optima.Core.Abstractions;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Holds one WLAN client session open for the length of a match and asks each connected interface,
/// through it, for media streaming mode and for background scanning off. Media streaming mode is
/// open to any program; Windows may refuse the scan setting to one without administrator rights,
/// and the status says which it accepted. Called by one pass at a time.
/// </summary>
public sealed class WindowsWifiScanPause : IWifiScanPause
{
    private readonly ILogger<WindowsWifiScanPause> _logger;
    private WlanNative.WlanHandle? _session;

    public WindowsWifiScanPause(ILogger<WindowsWifiScanPause> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<WifiInterfaceHold>? Hold()
    {
        _session ??= WlanNative.Open();
        if (_session is not { } session)
        {
            return null;
        }
        try
        {
            return [.. WlanNative.ConnectedInterfaces(session).Select(i => new WifiInterfaceHold(
                i.Name,
                WlanNative.Set(session, i.Id, WlanNative.wlan_intf_opcode_media_streaming_mode, true),
                WlanNative.Set(session, i.Id, WlanNative.wlan_intf_opcode_background_scan_enabled, false)))];
        }
        catch
        {
            // The handle may have gone stale (the WLAN service restarted mid-match), and every later
            // call on it would fail the same way. Dropping it lets the next pass open a fresh one.
            Release();
            throw;
        }
    }

    public void Release()
    {
        _session?.Dispose();
        _session = null;
        _logger.LogDebug("Wi-Fi session closed");
    }
}
