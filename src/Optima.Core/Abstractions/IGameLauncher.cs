using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>One strategy for starting the game (§5).</summary>
public interface IGameLauncher
{
    string Name { get; }

    int Order { get; }

    Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default);

    Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default);
}
