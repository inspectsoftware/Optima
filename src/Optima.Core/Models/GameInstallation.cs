namespace Optima.Core.Models;

/// <summary>Resolved facts about the Google Play Games for PC installation.</summary>
public sealed record GooglePlayGamesInstallation
{
    public required string InstallDirectory { get; init; }
    public string Version { get; init; } = string.Empty;
    public string BootstrapperPath { get; init; } = string.Empty;
    public string ClientPath { get; init; } = string.Empty;
    public string EmulatorPath { get; init; } = string.Empty;
    public bool ProtocolHandlerRegistered { get; init; }
    public bool ServiceRunning { get; init; }
}

/// <summary>One installed Android game surfaced by Google Play Games.</summary>
public sealed record InstalledGame
{
    /// <summary>Android package id, e.g. com.criticalforceentertainment.criticalops.</summary>
    public required string PackageId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Full launch URI, e.g. googleplaygames://launch/?id=&lt;pkg&gt;.</summary>
    public string LaunchUri { get; init; } = string.Empty;

    public string ShortcutPath { get; init; } = string.Empty;

    public string IconPath { get; init; } = string.Empty;
}

public enum GameRuntimeState
{
    NotRunning,
    Starting,
    Running,
    Exited,
}
