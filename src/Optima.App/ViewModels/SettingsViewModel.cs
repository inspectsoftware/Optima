using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Optima.Core.Theming;

namespace Optima.App.ViewModels;

/// <summary>One selectable accent preset on the SETTINGS page.</summary>
public sealed record AccentPreset(string Name, string Hex);

/// <summary>SETTINGS page (§21/§28/§29): appearance, app options, detection overrides.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Services.PlayerSwitcherService _players;
    private readonly CopsApiClient _api;
    private readonly Optima.Core.Linking.BotLinkClient _botLink;
    private readonly Optima.Core.Protection.ShieldLoader _shield;

    public SettingsViewModel(
        SettingsService settings,
        Services.PlayerSwitcherService players,
        CopsApiClient api,
        Optima.Core.Linking.BotLinkClient botLink,
        Optima.Core.Protection.ShieldLoader shield)
    {
        _settings = settings;
        _players = players;
        _api = api;
        _botLink = botLink;
        _shield = shield;
        // The tracked player and the saved list also change from the title-bar switcher.
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public IReadOnlyList<string> ProviderOptions { get; } = ["Auto", "MttVdd", "Mock"];
    public IReadOnlyList<string> LogLevelOptions { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];
    public IReadOnlyList<string> CornerOptions { get; } = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public IReadOnlyList<double> OpacityOptions { get; } = [0.5, 0.65, 0.8, 1.0];
    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

    // In the order of MotionPolicy.Modes: System, On, Off.
    private static readonly string[] AnimationLabels = ["as Windows says", "always on", "off"];
    public IReadOnlyList<string> AnimationOptions => AnimationLabels;

    /// <summary>
    /// The priority Optima pins for the Google Play Games process that runs Critical Ops.
    /// Unchanged defers to the priority stored in the selected launch profile.
    /// </summary>

    public IReadOnlyList<AccentPreset> AccentPresets { get; } =
    [
        new("Gold", "#E8B45A"),
        new("Frost", "#6FB7E8"),
        new("Mint", "#7FD6A4"),
        new("Rose", "#E88A9E"),
        new("Violet", "#A98BE8"),
        new("Slate", "#C7CFDD"),
    ];

    [RelayCommand]
    private void SelectAccent(AccentPreset preset) => AccentColor = preset.Hex;

    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _accentColor = AccentMath.DefaultAccentHex;
    // ---------------------------------------------------------------- PLAYER

    /// <summary>The one PLAYER box: an in-game name or an account id.</summary>
    [ObservableProperty] private string _playerQuery = string.Empty;

    /// <summary>Why the last Track press changed nothing (no such player), shown under the box.</summary>
    [ObservableProperty] private string _trackMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrackCommand))]
    private bool _isTracking;

    // The tracked player as stored. Tracking and switching write it straight to the settings, not
    // through the save bar: HOME, the session stats and Discord follow it the moment it changes.
    private string _trackedIgn = string.Empty;
    private long? _trackedAccountId;

    // Who the card is showing, so a settings save that leaves the player alone does not look them up again.
    private (string Ign, long? Id)? _cardFor;

    [ObservableProperty] private bool _hasTrackedPlayer;
    [ObservableProperty] private string _trackedName = string.Empty;
    [ObservableProperty] private string _trackedInitial = string.Empty;
    [ObservableProperty] private string _trackedIdText = string.Empty;

    /// <summary>Level, rank and ranked record, once the API has answered for this player.</summary>
    [ObservableProperty] private string _trackedSummary = string.Empty;

    [ObservableProperty] private string _trackedStatus = string.Empty;

    /// <summary>False when the status line is a warning (API unreachable, a doubtful id).</summary>
    [ObservableProperty] private bool _trackedStatusOk = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTrackedCommand))]
    private bool _isTrackedSaved;

    /// <summary>Players saved in Optima, shown as chips; the tracked one is marked active.</summary>
    public ObservableCollection<PlayerAccount> SavedPlayers { get; } = [];

    private const string FollowLine = "Tracking · HOME, sessions and Discord follow this player";

    private bool CanTrack() => !IsTracking;

    [RelayCommand(CanExecute = nameof(CanTrack))]
    private async Task TrackAsync()
    {
        var typed = PlayerQuery.Trim();
        if (typed.Length == 0)
        {
            TrackMessage = "Type an in-game name or an account ID first.";
            return;
        }

        IsTracking = true;
        TrackMessage = string.Empty;
        CopsLookupResult lookup;
        try
        {
            lookup = await TrackedPlayerQuery.LookupAsync(_api, typed);
        }
        catch (Exception)
        {
            lookup = new CopsLookupResult(CopsLookupStatus.Error, null, "The stats API could not be reached.");
        }
        finally
        {
            IsTracking = false;
        }

        var decision = TrackedPlayerQuery.Decide(typed, lookup);
        if (!decision.Tracks)
        {
            TrackMessage = TrackedPlayerQuery.AsAccountId(typed) is null
                ? $"No player called {typed} was found. Check the spelling, or use the account ID from the profile."
                : $"No player has the ID {typed}, and nobody is called that either.";
            return;
        }

        // The card first, so the settings change that follows finds it already showing this player.
        if (decision.Outcome == TrackOutcome.Found)
        {
            var profile = lookup.Profile!;
            ShowTracked(decision.Ign, decision.AccountId, profile,
                CopsApiClient.LooksLikePlaceholderProfile(profile) ? lookup.Detail : FollowLine,
                ok: !CopsApiClient.LooksLikePlaceholderProfile(profile));
        }
        else
        {
            ShowTracked(decision.Ign, decision.AccountId, null,
                "The stats API could not be reached, so this is tracked as typed. Stats fill in once it answers.", ok: false);
        }

        try
        {
            await _settings.UpdateSettingsAsync(s => s with
            {
                PlayerIgn = decision.Ign,
                PlayerAccountId = decision.AccountId,
            });
            PlayerQuery = string.Empty;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            TrackMessage = "The player could not be saved: " + ex.Message;
            _cardFor = null;
            LoadPlayer(await _settings.GetSettingsAsync());
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveTracked))]
    private async Task SaveTrackedAsync()
    {
        try
        {
            await _players.SaveAccountAsync(_trackedIgn, _trackedAccountId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            TrackMessage = ex.Message;
        }
    }

    private bool CanSaveTracked() => !IsTrackedSaved;

    /// <summary>A saved chip: makes that player the tracked one.</summary>
    [RelayCommand]
    private async Task UseSavedAsync(PlayerAccount? account)
    {
        if (account is null || account.Matches(_trackedIgn, _trackedAccountId))
        {
            return;
        }
        TrackMessage = string.Empty;
        await _players.SwitchToAsync(account);
    }

    /// <summary>The x on a saved chip. The tracked player stays tracked; only the saved entry goes.</summary>
    [RelayCommand]
    private async Task RemoveSavedAsync(PlayerAccount? account)
    {
        if (account is not { Key.Length: > 0 })
        {
            return;
        }
        await _players.RemoveAccountAsync(account.Key);
    }

    // Every change below (tracking, saving, switching, removing) arrives here through the settings
    // change, the same way a switch made from the title bar does.
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Raised on whatever thread finished the save; the card is bound to the UI.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => LoadPlayer(settings));
            return;
        }
        LoadPlayer(settings);
    }

    /// <summary>The PLAYER card and the saved chips as the settings have them.</summary>
    private void LoadPlayer(AppSettings settings)
    {
        _trackedIgn = settings.PlayerIgn.Trim();
        _trackedAccountId = settings.PlayerAccountId;

        SavedPlayers.Clear();
        foreach (var account in settings.SavedAccounts)
        {
            SavedPlayers.Add(account with { IsActive = account.Matches(_trackedIgn, _trackedAccountId) });
        }

        var identity = (_trackedIgn, _trackedAccountId);
        if (_cardFor != identity)
        {
            ShowTracked(_trackedIgn, _trackedAccountId, null, FollowLine, ok: true);
            if (HasTrackedPlayer)
            {
                _ = FillTrackedSummaryAsync(identity);
            }
        }
        IsTrackedSaved = SavedPlayers.Any(a => a.IsActive);
    }

    private void ShowTracked(string ign, long? accountId, CopsPlayerProfile? profile, string status, bool ok)
    {
        _cardFor = (ign, accountId);
        var shown = new PlayerAccount { Ign = profile?.Name ?? ign, AccountId = accountId };
        HasTrackedPlayer = ign.Length > 0 || accountId is > 0;
        TrackedName = shown.DisplayName;
        TrackedInitial = TrackedName.Length > 0 ? TrackedName[..1].ToUpperInvariant() : string.Empty;
        TrackedIdText = accountId is { } id ? "ID " + id.ToString(CultureInfo.InvariantCulture) : "no ID yet";
        TrackedSummary = profile is null ? string.Empty : TrackedPlayerQuery.Summary(profile);
        TrackedStatus = status;
        TrackedStatusOk = ok;
        IsTrackedSaved = SavedPlayers.Any(a => a.Matches(ign, accountId));
    }

    /// <summary>Looks the stored player up for the card's level and rank line. Quietly: the card works without it.</summary>
    private async Task FillTrackedSummaryAsync((string Ign, long? Id) identity)
    {
        CopsLookupResult lookup;
        try
        {
            lookup = await _api.LookupPlayerAsync(identity.Ign, identity.Id);
        }
        catch (Exception)
        {
            return;
        }

        // The player may have changed while the lookup ran.
        if (_cardFor != identity)
        {
            return;
        }
        if (lookup.IsFound)
        {
            TrackedName = lookup.Profile!.Name;
            TrackedInitial = TrackedName.Length > 0 ? TrackedName[..1].ToUpperInvariant() : string.Empty;
            TrackedSummary = TrackedPlayerQuery.Summary(lookup.Profile);
        }
        else if (lookup.Status == CopsLookupStatus.Error)
        {
            TrackedStatus = "The stats API could not be reached; the stats show once it answers.";
            TrackedStatusOk = false;
        }
        else
        {
            TrackedStatus = "The stats API does not know this player. Track them again by name or ID.";
            TrackedStatusOk = false;
        }
    }

    [ObservableProperty] private bool _discordPresenceEnabled = true;
    [ObservableProperty] private bool _discordPresenceInLauncher = true;

    private bool _discordBotLinkEnrolled;
    /// <summary>The field-by-field Discord card choice, edited in the presence chooser window.</summary>
    [ObservableProperty] private DiscordPresenceOptions _discordOptions = new();

    [ObservableProperty] private string _discordOptionsSummary = string.Empty;

    /// <summary>
    /// Where the OptimaBot link API answers. Saved with the rest of the settings and prefilled with
    /// Optima's community bot, so linking works untouched; a self-hosted bot's address replaces it.
    /// </summary>
    [ObservableProperty] private string _discordBotUrl = Optima.Core.Linking.BotLinkClient.DefaultBaseUrl;

    /// <summary>
    /// The Discord channel webhook the bot posts this account's matches and rank changes to after
    /// linking. Empty links without tracking, which is the default.
    /// </summary>
    [ObservableProperty] private string _discordBotWebhookUrl = string.Empty;

    /// <summary>The link as the bot last confirmed it, in one line for the Settings row.</summary>
    [ObservableProperty] private string _discordBotLinkSummary = "not linked";

    private string _discordBotLinkTag = string.Empty;
    private string _discordBotLinkedPlayer = string.Empty;
    private DateTimeOffset? _discordBotLinkedAt;

    private void UpdateDiscordLinkSummary()
        => DiscordBotLinkSummary = _discordBotLinkedPlayer.Length switch
        {
            0 => "not linked",
            _ when _discordBotLinkTag.Length > 0 => $"linked to {_discordBotLinkedPlayer} as {_discordBotLinkTag}",
            _ => $"linked to {_discordBotLinkedPlayer}",
        };

    /// <summary>
    /// Opens the link dialog and, when the bot confirms a link, remembers it. The write goes straight to
    /// the store rather than through the save bar: the link exists on the bot the moment it is written,
    /// so a local copy still sitting unsaved would be a Settings page that disagrees with reality.
    /// </summary>
    [RelayCommand]
    private async Task LinkDiscordAsync()
    {
        var baseUrl = Optima.Core.Linking.BotLinkClient.NormalizeBaseUrl(DiscordBotUrl)
            ?? Optima.Core.Linking.BotLinkClient.DefaultBaseUrl;
        var accountId = _trackedAccountId;

        // The tracker webhook is optional, but a typo in it would look like a working tracker that
        // never posts anything, and the bot refuses one it cannot post to anyway: catch it here, where
        // the field being fixed is on screen.
        var webhook = Optima.Core.Linking.TrackerWebhook.Normalize(DiscordBotWebhookUrl);
        if (DiscordBotWebhookUrl.Trim().Length > 0 && webhook is null)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = "That tracker webhook does not look like a Discord webhook URL. Copy it from the "
                + "channel's Integrations settings in Discord, or clear the field to link without tracking.";
            SaveBarVisible = true;
            return;
        }

        // What the account is currently linked to, fetched first so the dialog can say what it is about to
        // replace. A bot that cannot be reached is not an error here: the dialog explains it.
        Optima.Core.Linking.BotLinkStatusResponse? status = null;
        if (accountId is { } known)
        {
            var lookup = await _botLink.GetStatusAsync(baseUrl, known);
            if (lookup.Ok)
            {
                status = lookup.Value;
            }
        }

        // This PC's protected play key travels with the claim: the code proves who is linking, so it
        // is the one moment the key can be bound to them. Optima Shield holds the key and prints its
        // public half. A build without the module, or a key Windows will not make, is not a reason
        // to refuse the link; it links without protected play and the summary says so.
        var deviceKey = await Task.Run(_shield.PublicKey);
        // Linking a PC that has the module turns protected play on, so what it does is read first.
        // Not reading it is not linking.
        if (deviceKey is not null && !Views.ProtectedPlayWindow.Ask())
        {
            return;
        }

        var window = new Views.DiscordLinkWindow(_botLink, baseUrl, accountId, _trackedIgn, status, webhook, deviceKey)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (window.ShowDialog() != true || window.Result is not { Ok: true } claim)
        {
            return;
        }

        _discordBotLinkTag = claim.DiscordTag ?? string.Empty;
        _discordBotLinkedPlayer = claim.PlayerName ?? string.Empty;
        _discordBotLinkedAt = claim.LinkedAt;
        // Asked, not assumed: a bot from before protected play takes the link and drops the key.
        // An answer that did not arrive is not a no: the link was made a moment ago, and if the key
        // really was dropped, the module's first start is refused in the bot's own words.
        var after = deviceKey is not null && claim.AccountId is { } linkedId
            ? await _botLink.GetStatusAsync(baseUrl, linkedId)
            : null;
        _discordBotLinkEnrolled = after is { Ok: false } or { Value.ProtectionReady: true };
        // The module runs only with this file, and only for the account that was just enrolled.
        if (_discordBotLinkEnrolled && claim.AccountId is { } enrolled)
        {
            _shield.WriteLink(enrolled, baseUrl);
        }
        else
        {
            _shield.RemoveLink();
        }
        DiscordBotUrl = baseUrl;
        DiscordBotWebhookUrl = webhook ?? string.Empty;
        UpdateDiscordLinkSummary();

        await _settings.UpdateSettingsAsync(s => s with
        {
            DiscordBotUrl = baseUrl,
            DiscordBotWebhookUrl = webhook ?? string.Empty,
            DiscordBotLinkTag = _discordBotLinkTag,
            DiscordBotLinkedPlayer = _discordBotLinkedPlayer,
            DiscordBotLinkedAt = _discordBotLinkedAt,
            DiscordBotLinkEnrolled = _discordBotLinkEnrolled,
        });

        SaveBarMark = "[ OK ]";
        SaveBarText = webhook is null
            ? $"Linked to {_discordBotLinkedPlayer}. Run /searchplayer in Discord to see your own card."
            : $"Linked to {_discordBotLinkedPlayer}. New matches and rank changes will be posted to your tracker webhook.";
        SaveBarVisible = true;
    }

    /// <summary>
    /// Sends one sample report image to the webhook field through the bot, so the channel and the URL
    /// can be checked before a real match needs them. The field is used as typed; nothing is saved.
    /// </summary>
    [RelayCommand]
    private async Task TestTrackerAsync()
    {
        var typed = DiscordBotWebhookUrl.Trim();
        if (typed.Length == 0)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = "Paste the channel's webhook URL first, then press the test button.";
            SaveBarVisible = true;
            return;
        }

        var webhook = Optima.Core.Linking.TrackerWebhook.Normalize(typed);
        if (webhook is null)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = "That tracker webhook does not look like a Discord webhook URL. Copy it from the "
                + "channel's Integrations settings in Discord.";
            SaveBarVisible = true;
            return;
        }

        var baseUrl = Optima.Core.Linking.BotLinkClient.NormalizeBaseUrl(DiscordBotUrl)
            ?? Optima.Core.Linking.BotLinkClient.DefaultBaseUrl;

        SaveBarMark = "[ … ]";
        SaveBarText = "Sending a sample report to the webhook...";
        SaveBarVisible = true;

        var result = await _botLink.TestTrackerAsync(baseUrl, webhook);
        var answer = result.Value;
        if (answer is { Ok: true })
        {
            SaveBarMark = "[ OK ]";
            SaveBarText = answer.Message.Length > 0
                ? answer.Message
                : "Test image sent. Check the channel for the sample report.";
        }
        else
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = answer?.Message is { Length: > 0 } message ? message : result.Message;
        }

        SaveBarVisible = true;
    }

    /// <summary>
    /// Opens the presence chooser. The window edits its own copy, so a cancelled dialog changes
    /// nothing and only an accepted one can light up the save bar.
    /// </summary>
    [RelayCommand]
    private void ChooseDiscordPresence()
    {
        var window = new Views.DiscordPresenceWindow(DiscordOptions)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (window.ShowDialog() != true || window.Result is null)
        {
            return;
        }
        DiscordOptions = window.Result;
        UpdateDiscordOptionsSummary();
    }

    private void UpdateDiscordOptionsSummary()
    {
        var options = DiscordOptions;
        var parts = new List<string>();
        if (options.ShowPlayerName)
        {
            parts.Add("name");
        }
        if (options.ShowRank)
        {
            parts.Add("rank");
        }
        if (options.ShowRankEmblem)
        {
            parts.Add("emblem");
        }
        if (options.ShowRankedRecord)
        {
            parts.Add("record");
        }
        if (options.ShowRankedRating)
        {
            parts.Add("rating");
        }
        if (options.ShowElapsedTime)
        {
            parts.Add("timer");
        }
        DiscordOptionsSummary = parts.Count == 0 ? "game name only" : string.Join(" · ", parts);
    }
    [ObservableProperty] private string _discordApplicationId = string.Empty;

    [ObservableProperty] private string _provider = "Auto";
    [ObservableProperty] private bool _enableFrametimeCapture = true;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private bool _developerMode;
    [ObservableProperty] private bool _keepInTrayOnClose;
    [ObservableProperty] private bool _checkForUpdatesAtStart = true;
    /// <summary>One of <see cref="AnimationOptions"/>.</summary>
    [ObservableProperty] private string _animations = AnimationLabels[0];
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private string _overlayCorner = "TopRight";
    [ObservableProperty] private double _overlayOpacity = 0.8;
    [ObservableProperty] private bool _overlayShowNetwork = true;
    [ObservableProperty] private string _networkReferenceHost = "1.1.1.1";
    [ObservableProperty] private bool _enableWatchMode;
    [ObservableProperty] private bool _autoRelaunchOnCrash;
    [ObservableProperty] private bool _useMockMetricsProvider;
    [ObservableProperty] private string _vddSettingsPath = string.Empty;
    [ObservableProperty] private string _manualInstallPath = string.Empty;
    [ObservableProperty] private string _customLaunchCommand = string.Empty;

    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _saveBarVisible;
    [ObservableProperty] private string _saveBarText = string.Empty;
    [ObservableProperty] private string _saveBarMark = "[ ! ]";

    private DispatcherTimer? _confirmTimer;

    // Values as of the last load or save; every property change is compared against this so the
    // pinned save bar appears the moment anything differs (multiple users missed the old button).
    private IReadOnlyDictionary<string, object?> _savedValues = new Dictionary<string, object?>();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Not for the bar's own three: with a change pending, every message set on the bar (a test
        // result, an error, "account saved") was replaced by the unsaved warning the moment it was set.
        if (e.PropertyName is not (nameof(HasUnsavedChanges) or nameof(SaveBarMark) or nameof(SaveBarText) or nameof(SaveBarVisible)))
        {
            RefreshUnsavedState();
        }
    }

    private void CaptureSavedValues() => _savedValues = NormalizedValues();

    private Dictionary<string, object?> NormalizedValues() => new()
    {
        [nameof(Theme)] = Theme,
        [nameof(AutoRelaunchOnCrash)] = AutoRelaunchOnCrash,
        [nameof(AccentColor)] = AccentColor.Trim(),
        [nameof(DiscordPresenceEnabled)] = DiscordPresenceEnabled,
        [nameof(DiscordPresenceInLauncher)] = DiscordPresenceInLauncher,
        [nameof(DiscordOptions)] = DiscordOptions,
        [nameof(DiscordApplicationId)] = DiscordApplicationId.Trim(),
        [nameof(DiscordBotUrl)] = DiscordBotUrl.Trim(),
        [nameof(DiscordBotWebhookUrl)] = DiscordBotWebhookUrl.Trim(),
        [nameof(Provider)] = Provider,
        [nameof(EnableFrametimeCapture)] = EnableFrametimeCapture,
        [nameof(LogLevel)] = LogLevel,
        [nameof(DeveloperMode)] = DeveloperMode,
        [nameof(KeepInTrayOnClose)] = KeepInTrayOnClose,
        [nameof(CheckForUpdatesAtStart)] = CheckForUpdatesAtStart,
        [nameof(Animations)] = Animations,
        [nameof(StartWithWindows)] = StartWithWindows,
        [nameof(OverlayEnabled)] = OverlayEnabled,
        [nameof(OverlayCorner)] = OverlayCorner,
        [nameof(OverlayOpacity)] = OverlayOpacity,
        [nameof(OverlayShowNetwork)] = OverlayShowNetwork,
        [nameof(NetworkReferenceHost)] = string.IsNullOrWhiteSpace(NetworkReferenceHost) ? "1.1.1.1" : NetworkReferenceHost.Trim(),
        [nameof(EnableWatchMode)] = EnableWatchMode,
        [nameof(UseMockMetricsProvider)] = UseMockMetricsProvider,
        [nameof(VddSettingsPath)] = VddSettingsPath.Trim(),
        [nameof(ManualInstallPath)] = ManualInstallPath.Trim(),
        [nameof(CustomLaunchCommand)] = CustomLaunchCommand.Trim(),
    };

    private void RefreshUnsavedState()
    {
        var current = NormalizedValues();
        HasUnsavedChanges = _savedValues.Count > 0
            && (current.Count != _savedValues.Count
                || current.Any(kv => !Equals(kv.Value, _savedValues.GetValueOrDefault(kv.Key))));
        if (!HasUnsavedChanges)
        {
            return;
        }

        // Any further edit replaces the confirmation with the warning again.
        StopConfirmTimer();
        SaveBarMark = "[ ! ]";
        SaveBarText = "Unsaved changes. Settings apply when saved.";
        SaveBarVisible = true;
    }

    private void ShowSaveConfirmation(string message)
    {
        SaveBarMark = "[ OK ]";
        SaveBarText = message;
        SaveBarVisible = true;
        StopConfirmTimer();
        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _confirmTimer.Tick += (_, _) =>
        {
            StopConfirmTimer();
            if (!HasUnsavedChanges)
            {
                SaveBarVisible = false;
            }
        };
        _confirmTimer.Start();
    }

    private void StopConfirmTimer()
    {
        _confirmTimer?.Stop();
        _confirmTimer = null;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(ct);
        Theme = settings.Theme;
        AccentColor = settings.AccentColor;
        LoadPlayer(settings);
        DiscordPresenceEnabled = settings.DiscordPresenceEnabled;
        DiscordPresenceInLauncher = settings.DiscordPresenceInLauncher;
        DiscordOptions = settings.EffectivePresenceOptions;
        UpdateDiscordOptionsSummary();
        DiscordApplicationId = settings.DiscordApplicationId;
        DiscordBotUrl = string.IsNullOrWhiteSpace(settings.DiscordBotUrl)
            ? Optima.Core.Linking.BotLinkClient.DefaultBaseUrl
            : settings.DiscordBotUrl;
        DiscordBotWebhookUrl = settings.DiscordBotWebhookUrl;
        _discordBotLinkTag = settings.DiscordBotLinkTag;
        _discordBotLinkedPlayer = settings.DiscordBotLinkedPlayer;
        _discordBotLinkedAt = settings.DiscordBotLinkedAt;
        _discordBotLinkEnrolled = settings.DiscordBotLinkEnrolled;
        UpdateDiscordLinkSummary();
        Provider = settings.VirtualDisplayProvider;
        EnableFrametimeCapture = settings.EnableFrametimeCapture;
        LogLevel = settings.MinimumLogLevel;
        DeveloperMode = settings.DeveloperMode;
        KeepInTrayOnClose = settings.KeepInTrayOnClose;
        CheckForUpdatesAtStart = settings.CheckForUpdatesAtStart;
        Animations = AnimationLabels[Math.Max(0, Optima.Core.Theming.MotionPolicy.Modes.ToList().FindIndex(mode => string.Equals(mode, settings.EffectiveAnimations, StringComparison.OrdinalIgnoreCase)))];
        StartWithWindows = settings.StartWithWindows;
        OverlayEnabled = settings.OverlayEnabled;
        OverlayCorner = settings.OverlayCorner;
        OverlayOpacity = settings.OverlayOpacity;
        OverlayShowNetwork = settings.OverlayShowNetwork;
        NetworkReferenceHost = settings.NetworkReferenceHost;
        EnableWatchMode = settings.EnableWatchMode;
        AutoRelaunchOnCrash = settings.AutoRelaunchOnCrash;
        UseMockMetricsProvider = settings.UseMockMetricsProvider;
        VddSettingsPath = settings.VddSettingsPath ?? string.Empty;

        var rules = await _settings.GetDetectionRulesAsync(ct);
        ManualInstallPath = rules.ManualInstallPath ?? string.Empty;
        CustomLaunchCommand = rules.CustomLaunchCommand ?? string.Empty;

        CaptureSavedValues();
        HasUnsavedChanges = false;
        StopConfirmTimer();
        SaveBarVisible = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var accentValid = AccentMath.TryParse(AccentColor) is not null;

        await _settings.UpdateSettingsAsync(s => s with
        {
            Theme = Theme,
            AccentColor = accentValid ? AccentColor.Trim() : s.AccentColor,
            DiscordPresenceEnabled = DiscordPresenceEnabled,
            DiscordPresenceInLauncher = DiscordPresenceInLauncher,
            DiscordPresenceOptions = DiscordOptions,
            DiscordApplicationId = DiscordApplicationId.Trim(),
            DiscordBotUrl = string.IsNullOrWhiteSpace(DiscordBotUrl)
                ? Optima.Core.Linking.BotLinkClient.DefaultBaseUrl
                : DiscordBotUrl.Trim(),
            DiscordBotWebhookUrl = DiscordBotWebhookUrl.Trim(),
            VirtualDisplayProvider = Provider,
            EnableFrametimeCapture = EnableFrametimeCapture,
            MinimumLogLevel = LogLevel,
            DeveloperMode = DeveloperMode,
            KeepInTrayOnClose = KeepInTrayOnClose,
            CheckForUpdatesAtStart = CheckForUpdatesAtStart,
            Animations = Optima.Core.Theming.MotionPolicy.Modes[Math.Max(0, Array.IndexOf(AnimationLabels, Animations))],
            StartWithWindows = StartWithWindows,
            OverlayEnabled = OverlayEnabled,
            OverlayCorner = OverlayCorner,
            OverlayOpacity = OverlayOpacity,
            OverlayShowNetwork = OverlayShowNetwork,
            NetworkReferenceHost = string.IsNullOrWhiteSpace(NetworkReferenceHost) ? "1.1.1.1" : NetworkReferenceHost.Trim(),
            EnableWatchMode = EnableWatchMode,
            AutoRelaunchOnCrash = AutoRelaunchOnCrash,
            UseMockMetricsProvider = UseMockMetricsProvider,
            VddSettingsPath = string.IsNullOrWhiteSpace(VddSettingsPath) ? null : VddSettingsPath.Trim(),
        });

        var rules = await _settings.GetDetectionRulesAsync();
        await _settings.SaveDetectionRulesAsync(rules with
        {
            ManualInstallPath = string.IsNullOrWhiteSpace(ManualInstallPath) ? null : ManualInstallPath.Trim(),
            CustomLaunchCommand = string.IsNullOrWhiteSpace(CustomLaunchCommand) ? null : CustomLaunchCommand.Trim(),
        });

        var autostartError = ApplyStartWithWindows();

        App.LogLevelSwitch.MinimumLevel = LogStreamViewModel.ToSerilogLevel(LogLevel);

        if (!accentValid)
        {
            // Put back before the saved values are captured, or the box and the capture disagree
            // and the save bar never goes away.
            AccentColor = (await _settings.GetSettingsAsync()).AccentColor;
        }

        CaptureSavedValues();
        HasUnsavedChanges = false;

        if (!accentValid)
        {
            ShowSaveConfirmation("Settings saved. Accent color was not a valid hex value, so the previous accent was kept.");
        }
        else
        {
            ShowSaveConfirmation(autostartError is null
                ? "Settings saved."
                : "Settings saved, but the start-with-Windows entry could not be updated: " + autostartError);
        }
    }

    private string? ApplyStartWithWindows() => Services.AutostartService.Apply(StartWithWindows);

}
