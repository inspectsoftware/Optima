namespace Optima.Core.Abstractions;

/// <summary>
/// Google Play Games as something that can be counted, started and stopped. The game runs inside
/// it, so stopping it stops the game: nothing behind this interface is safe to call while a match
/// is on, and the callers are what make sure of that.
/// </summary>
public interface IPlatformControl
{
    /// <summary>How many Google Play Games processes are running, the platform's own and the emulator's.</summary>
    Task<int> CountRunningAsync(CancellationToken ct = default);

    /// <summary>Starts Google Play Games through its bootstrapper. False when it is not where detection looks.</summary>
    Task<bool> StartAsync(CancellationToken ct = default);

    /// <summary>Ends every Google Play Games process tree, a running game included. Returns how many were ended.</summary>
    Task<int> StopAsync(CancellationToken ct = default);
}
