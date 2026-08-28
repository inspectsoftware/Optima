using Optima.Core.Launch;

namespace Optima.Core.Abstractions;

/// <summary>
/// Finds the monitor the game window renders on, so the overlay can sit in one of its
/// corners. Read-only window enumeration; nothing touches the game.
/// </summary>
public interface IGameWindowLocator
{
    /// <summary>
    /// Work area, in device pixels, of the monitor showing the game window;
    /// null when no game window is visible.
    /// </summary>
    Task<OverlayRect?> GetGameMonitorWorkAreaAsync(CancellationToken ct = default);
}
