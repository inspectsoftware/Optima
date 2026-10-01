namespace Optima.Core.Models;

/// <summary>Persisted user settings (%LOCALAPPDATA%\Optima\config.json, §21).</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public string SelectedProfileName { get; init; } = "Default";

    public string Theme { get; init; } = "Dark";

    public string AccentColor { get; init; } = "#E8B45A";

    public string PlayerIgn { get; init; } = string.Empty;

    /// <summary>Critical Ops account id, used before the name for profile lookups (exact, survives renames).</summary>
    public long? PlayerAccountId { get; init; }

    /// <summary>Saved identities (main plus alternates) for the title-bar account switcher.</summary>
    public IReadOnlyList<PlayerAccount> SavedAccounts { get; init; } = [];

    /// <summary>Friends and clanmates tracked on the HOME player panel.</summary>
    public IReadOnlyList<PlayerAccount> TrackedPlayers { get; init; } = [];

    /// <summary>When on, a game that dies within five minutes of launch is relaunched once.</summary>
    public bool AutoRelaunchOnCrash { get; init; }

    public DateTimeOffset? LastAutoRelaunchAt { get; init; }

    /// <summary>Session-scoped competitive tweaks, applied on session start and restored on exit.</summary>
    public bool SessionTweakHdrOff { get; init; }

    public bool SessionTweakGameBarOff { get; init; }

    public bool SessionTweakFseOff { get; init; }

    public bool DiscordPresenceEnabled { get; init; } = true;

    public bool DiscordPresenceInLauncher { get; init; } = true;

    public string DiscordApplicationId { get; init; } = "1543421664904351794";

    public string LastKnownGameVersion { get; init; } = string.Empty;
    public bool DeveloperMode { get; init; }
    public string MinimumLogLevel { get; init; } = "Information";

    public string VirtualDisplayProvider { get; init; } = "Auto";

    public bool EnableFrametimeCapture { get; init; } = true;

    /// <summary>When on, sessions start Critical Ops inside the Google Play Games Developer Emulator via adb
    /// instead of the consumer client. The developer emulator exposes the graphics-stack toggles
    /// (Vulkan/DirectX, GPU device override) used for GPU-vs-CPU rendering experiments.</summary>
    public bool UseDeveloperEmulator { get; init; }

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

    /// <summary>Write time of the newest crash bundle the user has acknowledged on the Play tab.</summary>
    public DateTimeOffset? LastCrashSeenAt { get; init; }

    /// <summary>Saved clan rosters (one line per member token) for the Explore clan pane.</summary>
    public IReadOnlyList<ClanRoster> ClanRosters { get; init; } = [];
}

/// <summary>A pasted clan roster: the clan tag plus member tokens (account ids or in-game names).</summary>
public sealed record ClanRoster(string Tag, IReadOnlyList<string> Tokens);
