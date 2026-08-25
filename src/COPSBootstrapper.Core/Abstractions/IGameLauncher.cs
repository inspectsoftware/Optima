using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>
/// One strategy for starting the game (§5). Strategies are tried in order until one succeeds:
/// protocol URI → explicit bootstrapper exe → shortcut → user-defined command.
/// </summary>
public interface IGameLauncher
{
    string Name { get; }

    /// <summary>Lower comes first when picking a strategy.</summary>
    int Order { get; }

    Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default);

    /// <summary>Fires the launch. Returns false when this strategy could not start anything.</summary>
    Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default);
}
