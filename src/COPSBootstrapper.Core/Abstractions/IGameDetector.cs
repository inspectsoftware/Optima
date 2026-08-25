using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>Detects the Google Play Games installation and the target game.</summary>
public interface IGameDetector
{
    Task<GooglePlayGamesInstallation?> DetectPlatformAsync(CancellationToken ct = default);

    /// <summary>All games installed through Google Play Games (from shortcut/URI scan).</summary>
    Task<IReadOnlyList<InstalledGame>> DetectInstalledGamesAsync(CancellationToken ct = default);

    /// <summary>The target game (Critical Ops) if installed.</summary>
    Task<InstalledGame?> DetectTargetGameAsync(CancellationToken ct = default);
}
