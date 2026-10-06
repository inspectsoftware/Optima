using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Theming;

namespace Optima.App.ViewModels;

/// <summary>One selectable accent preset on the SETTINGS page.</summary>
public sealed record AccentPreset(string Name, string Hex);

/// <summary>SETTINGS page (§21/§28/§29): appearance, app options, detection overrides.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Services.PlayerSwitcherService _players;
    private readonly Optima.Core.Linking.BotLinkClient _botLink;

    public SettingsViewModel(
        SettingsService settings,
        Services.PlayerSwitcherService players,
        Optima.Core.Linking.BotLinkClient botLink)
    {
        _settings = settings;
        _players = players;
        _botLink = botLink;
    }

    public IReadOnlyList<string> ProviderOptions { get; } = ["Auto", "MttVdd", "Mock"];
    public IReadOnlyList<string> LogLevelOptions { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];
    public IReadOnlyList<string> CornerOptions { get; } = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public IReadOnlyList<double> OpacityOptions { get; } = [0.5, 0.65, 0.8, 1.0];
    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

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
    [ObservableProperty] private string _playerIgn = string.Empty;
    [ObservableProperty] private string _playerAccountId = string.Empty;
    [ObservableProperty] private string _saveAccountLabel = string.Empty;
    [ObservableProperty] private string _newTrackedIgn = string.Empty;
    [ObservableProperty] private string _newTrackedAccountId = string.Empty;
    public ObservableCollection<PlayerAccount> TrackedPlayers { get; } = [];

    [RelayCommand]
    private async Task SaveAsAccountAsync()
    {
        try
        {
            await _players.SaveCurrentAsAccountAsync(SaveAccountLabel);
            SaveAccountLabel = string.Empty;
            SaveBarMark = "[ OK ]";
            SaveBarText = "Account saved. It is now in the switcher at the top of the window.";
            SaveBarVisible = true;
        }
        catch (InvalidOperationException ex)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = ex.Message;
            SaveBarVisible = true;
        }
    }

    [RelayCommand]
    private async Task TrackPlayerAsync()
    {
        var ign = NewTrackedIgn.Trim();
        if (ign.Length == 0)
        {
            SaveBarMark = "[ ! ]";
            SaveBarText = "Give the player's in-game name first.";
            SaveBarVisible = true;
            return;
        }
        long? accountId = long.TryParse(NewTrackedAccountId.Trim(), out var id) && id > 0 ? id : null;
        // One instance for both: the row has to carry the key that was stored, or it cannot be removed.
        var tracked = new PlayerAccount { Ign = ign, AccountId = accountId };
        await _players.AddTrackedAsync(tracked);
        TrackedPlayers.Add(tracked);
        NewTrackedIgn = string.Empty;
        NewTrackedAccountId = string.Empty;
        SaveBarMark = "[ OK ]";
        SaveBarText = $"Tracking {ign}. They appear on HOME beside your own stats.";
        SaveBarVisible = true;
    }

    [RelayCommand]
    private async Task RemoveTrackedAsync(PlayerAccount account)
    {
        await _players.RemoveTrackedAsync(account.Key);
        TrackedPlayers.Remove(account);
    }

    [ObservableProperty] private bool _discordPresenceEnabled = true;
    [ObservableProperty] private bool _discordPresenceInLauncher = true;
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
        long? accountId = long.TryParse(PlayerAccountId.Trim(), out var id) && id > 0 ? id : null;

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

        var window = new Views.DiscordLinkWindow(_botLink, baseUrl, accountId, PlayerIgn.Trim(), status, webhook)
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
        if (options.ShowFps)
        {
            parts.Add("fps");
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
    [ObservableProperty] private bool _followWindowsMotion = true;
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
        if (e.PropertyName is not nameof(HasUnsavedChanges))
        {
            RefreshUnsavedState();
        }

        // The player identity is persisted as it is typed (debounced): it must survive an
        // app close or a crash without hunting for the Save button first.
        if (e.PropertyName is nameof(PlayerIgn) or nameof(PlayerAccountId))
        {
            RestartIdentityAutosave();
        }
    }

    private DispatcherTimer? _identityAutosaveTimer;

    private void RestartIdentityAutosave()
    {
        if (_savedValues.Count == 0)
        {
            // Not initialized yet (settings still loading); do not persist half-loaded text.
            return;
        }
        if (_identityAutosaveTimer is null)
        {
            _identityAutosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _identityAutosaveTimer.Tick += OnIdentityAutosaveTick;
        }
        _identityAutosaveTimer.Stop();
        _identityAutosaveTimer.Start();
    }

    private async void OnIdentityAutosaveTick(object? sender, EventArgs e)
    {
        var timer = (DispatcherTimer)sender!;
        timer.Stop();
        try
        {
            // An empty box clears the id; text that is not a number yet keeps the stored one.
            var idText = PlayerAccountId.Trim();
            var idValid = long.TryParse(idText, out var id) && id > 0;
            await _settings.UpdateSettingsAsync(s => s with
            {
                PlayerIgn = PlayerIgn.Trim(),
                PlayerAccountId = idValid ? id : idText.Length == 0 ? null : s.PlayerAccountId,
            });
        }
        catch
        {
            // Autosave is best-effort; the Save button remains the authoritative path.
        }
    }

    private void CaptureSavedValues() => _savedValues = NormalizedValues();

    private Dictionary<string, object?> NormalizedValues() => new()
    {
        [nameof(Theme)] = Theme,
        [nameof(AutoRelaunchOnCrash)] = AutoRelaunchOnCrash,
        [nameof(AccentColor)] = AccentColor.Trim(),
        [nameof(PlayerIgn)] = PlayerIgn.Trim(),
        [nameof(PlayerAccountId)] = PlayerAccountId.Trim(),
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
        [nameof(FollowWindowsMotion)] = FollowWindowsMotion,
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
        PlayerIgn = settings.PlayerIgn;
        PlayerAccountId = settings.PlayerAccountId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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
        UpdateDiscordLinkSummary();
        Provider = settings.VirtualDisplayProvider;
        EnableFrametimeCapture = settings.EnableFrametimeCapture;
        LogLevel = settings.MinimumLogLevel;
        DeveloperMode = settings.DeveloperMode;
        KeepInTrayOnClose = settings.KeepInTrayOnClose;
        FollowWindowsMotion = settings.FollowWindowsMotion;
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

        TrackedPlayers.Clear();
        foreach (var tracked in settings.TrackedPlayers)
        {
            TrackedPlayers.Add(tracked);
        }

        CaptureSavedValues();
        HasUnsavedChanges = false;
        StopConfirmTimer();
        SaveBarVisible = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var accentValid = AccentMath.TryParse(AccentColor) is not null;

        long? accountId = null;
        var accountText = PlayerAccountId.Trim();
        var accountValid = true;
        if (accountText.Length > 0)
        {
            if (long.TryParse(accountText, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                accountId = parsed;
            }
            else
            {
                accountValid = false;
            }
        }

        await _settings.UpdateSettingsAsync(s => s with
        {
            Theme = Theme,
            AccentColor = accentValid ? AccentColor.Trim() : s.AccentColor,
            PlayerIgn = PlayerIgn.Trim(),
            PlayerAccountId = accountValid ? accountId : s.PlayerAccountId,
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
            FollowWindowsMotion = FollowWindowsMotion,
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

        App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(LogLevel);

        if (!accountValid)
        {
            PlayerAccountId = (await _settings.GetSettingsAsync()).PlayerAccountId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (!accentValid)
        {
            // Put back before the saved values are captured, or the box and the capture disagree
            // and the save bar never goes away.
            AccentColor = (await _settings.GetSettingsAsync()).AccentColor;
        }

        CaptureSavedValues();
        HasUnsavedChanges = false;

        if (!accentValid && !accountValid)
        {
            ShowSaveConfirmation("Settings saved. The accent was not valid hex and the account id was not a number, so both were kept as they were.");
        }
        else if (!accentValid)
        {
            ShowSaveConfirmation("Settings saved. Accent color was not a valid hex value, so the previous accent was kept.");
        }
        else if (!accountValid)
        {
            ShowSaveConfirmation("Settings saved. The account id was not a number, so it was left unchanged; use the id shown on your Critical Ops profile page.");
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
