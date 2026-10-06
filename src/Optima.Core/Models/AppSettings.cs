using System.Text.Json.Serialization;

namespace Optima.Core.Models;

/// <summary>Persisted user settings (%LOCALAPPDATA%\Optima\config.json, §21).</summary>
public sealed record AppSettings
{
    public bool FirstRunCompleted { get; init; }
    public string SelectedProfileName { get; init; } = "Default";

    /// <summary>
    /// CPU scheduling priority Optima gives the Google Play Games process that runs Critical Ops,
    /// stored as text like the other enum-shaped settings (see ProcessPriorityLevel). Unchanged, the
    /// default, leaves the choice to the selected launch profile.
    /// </summary>
    public string GamePriority { get; init; } = "Unchanged";

    /// <summary>
    /// BOOST: keep <see cref="GamePriority"/> on the game's processes for as long as they run, also
    /// when the game was not started from Optima. Does nothing while the priority is Unchanged.
    /// </summary>
    public bool BoostPriorityGuardEnabled { get; init; } = true;

    /// <summary>
    /// BOOST's master switch (the dial). The switches below say which features it arms; none of
    /// them does anything while this is off.
    /// </summary>
    public bool BoostEnabled { get; init; }

    /// <summary>
    /// The settings as the Boost services should see them: with the master switch off every
    /// feature reads as off, while the stored ticks stay as the user left them.
    /// </summary>
    public AppSettings BoostEffective() => BoostEnabled
        ? this
        : this with
        {
            BoostPriorityGuardEnabled = false,
            BoostStandbyCleanerEnabled = false,
            BoostTimerResolutionEnabled = false,
            BoostDemoteBackgroundEnabled = false,
            BoostKeepCoresAwake = false,
            BoostGpuHighPerformance = false,
        };

    /// <summary>
    /// BOOST: while the game runs, purge the Windows standby list when free memory falls below
    /// <see cref="BoostStandbyFreeBelowMb"/> and the standby list is larger than
    /// <see cref="BoostStandbyAboveMb"/>. Off by default: it needs the elevated helper.
    /// </summary>
    public bool BoostStandbyCleanerEnabled { get; init; }

    public int BoostStandbyFreeBelowMb { get; init; } = Optima.Core.Boost.StandbyCleanerPolicy.DefaultFreeBelowMb;

    public int BoostStandbyAboveMb { get; init; } = Optima.Core.Boost.StandbyCleanerPolicy.DefaultStandbyAboveMb;

    /// <summary>BOOST: hold a fine system timer while the game is on screen. Off by default; what it reaches depends on the Windows version.</summary>
    public bool BoostTimerResolutionEnabled { get; init; }

    /// <summary>The resolution to hold, in milliseconds as text: "1.0" or "0.5".</summary>
    public string BoostTimerResolution { get; init; } = Optima.Core.Boost.TimerResolutionPolicy.OneMillisecond;

    /// <summary>
    /// BOOST: while the game is on screen, drop the programs in <see cref="BoostDemoteProcessNames"/>
    /// to below-normal priority and efficiency mode, and put them back afterwards. Nothing is closed.
    /// </summary>
    public bool BoostDemoteBackgroundEnabled { get; init; }

    /// <summary>Process names without ".exe". Starts as the built-in list; the BOOST page edits it.</summary>
    public IReadOnlyList<string> BoostDemoteProcessNames { get; init; } = Optima.Core.Launch.BackgroundDemotionService.DefaultProcessNames;

    /// <summary>BOOST: keep every processor core awake (no core parking) while the game is on screen.</summary>
    public bool BoostKeepCoresAwake { get; init; }

    /// <summary>BOOST: Windows' per-app graphics preference "High performance" for the game's executable.</summary>
    public bool BoostGpuHighPerformance { get; init; }

    /// <summary>
    /// The game's executable as last seen running. The graphics preference is keyed by it, and
    /// remembering it is what lets the preference be removed while the game is closed.
    /// </summary>
    public string? BoostGamePath { get; init; }

    public string Theme { get; init; } = "Dark";

    public string AccentColor { get; init; } = "#E8B45A";

    public string PlayerIgn { get; init; } = string.Empty;

    /// <summary>Critical Ops account id, used before the name for profile lookups (exact, survives renames).</summary>
    public long? PlayerAccountId { get; init; }

    /// <summary>Saved identities (main plus alternates) for the title-bar account switcher.</summary>
    public IReadOnlyList<PlayerAccount> SavedAccounts { get; init; } = [];

    /// <summary>Friends and clanmates tracked on the HOME player panel.</summary>
    public IReadOnlyList<PlayerAccount> TrackedPlayers { get; init; } = [];

    /// <summary>
    /// The HOME dashboard layout: widget ids in the order they are shown. Null means the layout has
    /// never been edited, so the default one applies; an empty list is a deliberately empty HOME.
    /// </summary>
    public IReadOnlyList<string>? HomeWidgets { get; init; }

    /// <summary>When on, a game that dies within five minutes of launch is relaunched once.</summary>
    public bool AutoRelaunchOnCrash { get; init; }

    public DateTimeOffset? LastAutoRelaunchAt { get; init; }

    /// <summary>Session-scoped competitive tweaks, applied on session start and restored on exit.</summary>
    public bool SessionTweakHdrOff { get; init; }

    public bool SessionTweakGameBarOff { get; init; }

    public bool SessionTweakFseOff { get; init; }

    public bool DiscordPresenceEnabled { get; init; } = true;

    public bool DiscordPresenceInLauncher { get; init; } = true;

    /// <summary>
    /// How much of the player's identity the Discord card shows (Minimal/Standard/Full). Kept for
    /// configurations written before the field-by-field chooser existed; <see cref="EffectivePresenceOptions"/>
    /// turns it into one when no explicit choice is stored.
    /// </summary>
    public string DiscordPresenceDetail { get; init; } = "Standard";

    /// <summary>
    /// The field-by-field choice for the Discord card, set by the presence chooser in Settings.
    /// Null in configs that predate it.
    /// </summary>
    public DiscordPresenceOptions? DiscordPresenceOptions { get; init; }

    /// <summary>
    /// The options actually in force: the explicit chooser result when there is one, otherwise the
    /// legacy three-step detail turned into the equivalent option set. Unknown stored text falls
    /// back to Standard rather than throwing, matching how the rest of the enum-shaped settings load.
    /// </summary>
    [JsonIgnore]
    public DiscordPresenceOptions EffectivePresenceOptions => DiscordPresenceOptions
        ?? (string.Equals(DiscordPresenceDetail, "Minimal", StringComparison.OrdinalIgnoreCase)
            ? DiscordPresenceOptions.Minimal
            : string.Equals(DiscordPresenceDetail, "Full", StringComparison.OrdinalIgnoreCase)
                ? DiscordPresenceOptions.Full
                : DiscordPresenceOptions.Standard);

    public string DiscordApplicationId { get; init; } = "1543421664904351794";

    /// <summary>
    /// Where the OptimaBot link API answers. Empty means the default, which is Optima's community bot;
    /// a self-hosted bot's address is set here by editing config.json; the app has no field for it.
    /// </summary>
    public string DiscordBotUrl { get; init; } = Optima.Core.Linking.BotLinkClient.DefaultBaseUrl;

    /// <summary>
    /// True once the one-time move off the old built-in bot address has happened. While false, a
    /// DiscordBotUrl that is the legacy local default is treated as never chosen and replaced with
    /// the community bot; once true, whatever the field holds, local addresses included, is exactly
    /// what the user meant.
    /// </summary>
    public bool DiscordBotUrlMigrated { get; init; }

    /// <summary>
    /// Where the bot posts this account's matches and rank changes after linking, when the user pasted
    /// a Discord channel webhook into Settings. Empty means linking without tracking.
    /// </summary>
    public string DiscordBotWebhookUrl { get; init; } = string.Empty;

    // The last link the bot confirmed, kept so Settings can name it without a network round trip. The
    // bot remains the authority: these are a memory of its answer, not a second copy of the link.
    public string DiscordBotLinkTag { get; init; } = string.Empty;
    public string DiscordBotLinkedPlayer { get; init; } = string.Empty;
    public DateTimeOffset? DiscordBotLinkedAt { get; init; }

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

    /// <summary>Write time of the newest crash bundle the user has acknowledged on the Play tab.</summary>
    public DateTimeOffset? LastCrashSeenAt { get; init; }
}
