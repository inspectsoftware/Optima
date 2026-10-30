namespace Optima.Core.Abstractions;

/// <summary>
/// One connected Wi-Fi interface Optima asked to keep quiet, and which of the two requests Windows
/// accepted: media streaming mode on, and background scanning off.
/// </summary>
public sealed record WifiInterfaceHold(string Name, bool StreamingMode, bool ScanPaused);

/// <summary>
/// Optima's own session with the WLAN service. What it asks for lasts as long as the session: closing
/// it, or Optima ending in any way, takes both requests back.
/// </summary>
public interface IWifiScanPause
{
    /// <summary>
    /// Opens the session if it is not open yet, and asks every connected Wi-Fi interface for media
    /// streaming mode and for background scanning off. Asking again is harmless, and is what catches
    /// an interface that reconnected (a disconnect drops both). One entry per connected interface,
    /// none when no interface is connected, and null when this PC has no Wi-Fi at all.
    /// </summary>
    IReadOnlyList<WifiInterfaceHold>? Hold();

    /// <summary>Closes the session, which ends both requests on every interface.</summary>
    void Release();
}
