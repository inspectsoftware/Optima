using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>One strategy for starting the game (§5).</summary>
public interface IGameLauncher
{
    string Name { get; }

    int Order { get; }

    /// <summary>Whether this strategy may run at all. Standard strategies use the default (true);
    /// opt-in strategies return the user's choice.</summary>
    bool IsEnabled => true;

    /// <summary>Whether a failed launch ends the ladder instead of trying the next strategy.
    /// Opt-in strategies set this so their failure never silently starts the game another way.</summary>
    bool IsExclusive => false;

    Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default);

    Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default);
}
