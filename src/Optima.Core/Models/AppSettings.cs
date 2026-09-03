namespace Optima.Core.Models;

/// <summary>Persisted user settings (%LOCALAPPDATA%\Optima\config.json, §21).</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public string SelectedProfileName { get; init; } = "Default";

    public string Theme { get; init; } = "Dark";

    public string AccentColor { get; init; } = "#E8B45A";

    public string PlayerIgn { get; init; } = string.Empty;

    public bool DiscordPresenceEnabled { get; init; } = true;

    public bool DiscordPresenceInLauncher { get; init; } = true;

    public string DiscordApplicationId { get; init; } = "1543421664904351794";

    public string LastKnownGameVersion { get; init; } = string.Empty;
    public bool DeveloperMode { get; init; }
    public string MinimumLogLevel { get; init; } = "Information";

    public string VirtualDisplayProvider { get; init; } = "Auto";

    public bool EnableFrametimeCapture { get; init; } = true;

    public string? CachedGpgInstallDirectory { get; init; }
    public string? CachedGameLaunchUri { get; init; }

    public string? VddSettingsPath { get; init; }

    public IReadOnlyDictionary<string, DisplayOverride> DisplayOverrides { get; init; }
        = new Dictionary<string, DisplayOverride>();

    public bool HideInactiveDisplays { get; init; }

    public bool KeepInTrayOnClose { get; init; }

    public bool FollowWindowsMotion { get; init; } = true;

    public bool RailCollapsed { get; init; }

    public bool StartWithWindows { get; init; }

    public bool OverlayEnabled { get; init; }

    public string OverlayCorner { get; init; } = "TopRight";

    public double OverlayOpacity { get; init; } = 0.8;

    public bool OverlayShowNetwork { get; init; } = true;

    public string NetworkReferenceHost { get; init; } = "1.1.1.1";

    public bool EnableWatchMode { get; init; }

    public bool UseMockMetricsProvider { get; init; }
}
