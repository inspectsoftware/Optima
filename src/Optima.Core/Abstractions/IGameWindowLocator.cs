using Optima.Core.Launch;

namespace Optima.Core.Abstractions;

/// <summary>Finds the monitor the game window renders on, so the overlay can sit in one of its corners.</summary>
public interface IGameWindowLocator
{
    Task<OverlayRect?> GetGameMonitorWorkAreaAsync(CancellationToken ct = default);
}
