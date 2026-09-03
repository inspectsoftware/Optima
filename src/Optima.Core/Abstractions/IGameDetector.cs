using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Detects the Google Play Games installation and the target game.</summary>
public interface IGameDetector
{
    Task<GooglePlayGamesInstallation?> DetectPlatformAsync(CancellationToken ct = default);

    Task<IReadOnlyList<InstalledGame>> DetectInstalledGamesAsync(CancellationToken ct = default);

    Task<InstalledGame?> DetectTargetGameAsync(CancellationToken ct = default);
}
