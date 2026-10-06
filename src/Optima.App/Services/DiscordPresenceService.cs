using System.Windows;
using DiscordRPC;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Discord;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Core.Stats;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>
/// Discord Rich Presence, fed by the Watchdog's presence service.
///
/// What is pushed is described by <see cref="PresenceComposer"/>; this type owns everything the
/// composer deliberately does not know about: the RPC client, which art the application actually has,
/// the throttling, and the lifetime of the player badge. The card shows the app mark as the large
/// image, the player's rank emblem as the small image, clickable text and artwork, and the live
/// details in Discord's own status line.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    /// <summary>How often a running session re-pushes the card so the fps line stays fresh.</summary>
    private static readonly TimeSpan FpsPushInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long a looked-up player badge is trusted before it is re-read from the API.</summary>
    private static readonly TimeSpan BadgeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The housekeeping tick. It exists so the card can move for reasons other than a game or fps
    /// event: a stale badge, or art that failed to resolve the first time. Identical cards are
    /// dropped before they reach Discord, so an idle tick costs one string comparison.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly GamePresenceService _presence;
    private readonly SettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly CopsApiClient _cops;
    private readonly DiscordArtCatalog _artCatalog;
    private readonly ILogger<DiscordPresenceService> _logger;

    private readonly object _gate = new();

    /// <summary>
    /// The uploaded asset the card prefers for its large image, named after the file it comes from:
    /// upload <c>Assets/optima-presence.png</c> in the Discord Developer Portal (Rich Presence -&gt;
    /// Art Assets) keeping that filename and the key is derived from it. What the application actually
    /// has is read from Discord rather than assumed, because a key that does not exist renders as a
    /// question mark with nothing logged.
    /// </summary>
    private const string PresenceArtKey = "optima-presence";

    /// <summary>
    /// Where the card points while the application has no asset uploaded. This is the artwork as the
    /// published repository serves it: an older mark than the one in the app, but a usable image rather
    /// than a broken one, and it stops being used the moment the upload exists. Discord accepts a plain
    /// https URL in an image field, so a card is never artless.
    /// </summary>
    private const string PresenceArtUrl =
        "https://raw.githubusercontent.com/inspectsoftware/Optima/master/src/Optima.App/Assets/optima-presence.png";

    private static readonly Button[] PresenceButtons =
    [
        new Button { Label = "Join Discord", Url = "https://discord.gg/tktZe8fkmj" },
        new Button { Label = "Beta", Url = "https://github.com/inspectsoftware/Optima/releases" },
    ];

    private DiscordRpcClient? _client;
    private Timer? _tick;

    private volatile bool _enabled;
    private volatile bool _inLauncherEnabled;
    private volatile DiscordPresenceOptions _options = DiscordPresenceOptions.Standard;
    private bool _launcherVisible;
    // Once the user has actually shown the window, the launcher card stays on through a
    // tray hide: the app is still running, and presence used to vanish on minimize-to-tray.
    // An autostart (--tray) instance that has never been shown still never broadcasts.
    private bool _launcherEverShown;
    private DateTimeOffset _launcherVisibleSince = DateTimeOffset.Now;
    private string _applicationId = "";
    private bool _subscribed;

    // Cached from the last ApplySettings call so composition never blocks on settings I/O.
    private string _playerIgn = "";
    private long? _playerAccountId;
    private string _profileName = "";

    // Which art the application really has, read once per application id. Empty means "nothing known
    // yet", which is not the same as "nothing uploaded": both fall back to the public artwork, and the
    // answer is retried on the next tick so one failed lookup does not pin the fallback for the life
    // of the process.
    private IReadOnlyList<(string Name, ulong Id)> _uploadedAssets = [];
    private bool _artResolved;
    private bool _artResolving;

    private PlayerSeasonBadge? _badge;
    // Which player the cached badge belongs to, so a change of identity invalidates it immediately.
    private string _badgeName = "";
    private DateTimeOffset _badgeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
    private string _lastSignature = "";

    public DiscordPresenceService(
        GamePresenceService presence,
        SettingsService settings,
        IPerformanceMonitor monitor,
        CopsApiClient cops,
        DiscordArtCatalog artCatalog,
        ILogger<DiscordPresenceService> logger)
    {
        _presence = presence;
        _settings = settings;
        _monitor = monitor;
        _cops = cops;
        _artCatalog = artCatalog;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
        ApplySettings(settings);
        if (!_subscribed)
        {
            _settings.SettingsChanged += OnSettingsChanged;
            _presence.PresenceChanged += OnPresenceChanged;
            _monitor.MetricsUpdated += OnMetrics;
            _subscribed = true;
        }
        _tick ??= new Timer(_ => UpdatePresence(), null, TickInterval, TickInterval);
    }

    public void AttachLauncherWindow(Window window)
    {
        lock (_gate)
        {
            _launcherVisible = window.IsVisible;
            _launcherEverShown = window.IsVisible;
            _launcherVisibleSince = DateTimeOffset.Now;
        }
        window.IsVisibleChanged += (_, args) =>
        {
            lock (_gate)
            {
                var visible = args.NewValue is true;
                if (visible && !_launcherVisible)
                {
                    // New browsing stint: the elapsed timer restarts, tray time does not count.
                    _launcherVisibleSince = DateTimeOffset.Now;
                    _launcherEverShown = true;
                }
                _launcherVisible = visible;
            }
            UpdatePresence();
        };
        UpdatePresence();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => ApplySettings(settings);

    private void ApplySettings(AppSettings settings)
    {
        _playerIgn = settings.PlayerIgn;
        _playerAccountId = settings.PlayerAccountId;
        _profileName = settings.SelectedProfileName;
        _enabled = settings.DiscordPresenceEnabled;
        _inLauncherEnabled = settings.DiscordPresenceInLauncher;
        _options = settings.EffectivePresenceOptions;

        var newId = settings.DiscordApplicationId.Trim();
        if (!string.Equals(newId, _applicationId, StringComparison.Ordinal))
        {
            _applicationId = newId;
            // A different application has its own assets, so the art is looked up again.
            _artResolved = false;
            _uploadedAssets = [];
            TearDownClient();
        }

        // The rank the card shows comes from the profile, so a new identity invalidates the badge
        // rather than waiting out the rest of its lifetime with the previous player's rank on it.
        if (!string.Equals(_badgeName, _playerIgn, StringComparison.Ordinal))
        {
            _badge = null;
            _badgeAt = DateTimeOffset.MinValue;
        }

        if (!_enabled)
        {
            SafeClear();
        }
        else
        {
            UpdatePresence();
        }
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        // A new run starts with an empty fps history; push right away rather than on the next tick.
        _lastPush = DateTimeOffset.MinValue;
        UpdatePresence();
    }

    private void OnMetrics(object? sender, HardwareMetrics metrics)
    {
        // Live fps only matters while a session runs; throttled so the pipe stays quiet.
        if (_presence.Current != GamePresence.InGame
            || DateTimeOffset.Now - _lastPush < FpsPushInterval)
        {
            return;
        }
        UpdatePresence();
    }

    private void UpdatePresence()
    {
        try
        {
            lock (_gate)
            {
                if (!_enabled || !TryEnsureClient())
                {
                    return;
                }

                RefreshArtIfNeeded();
                RefreshBadgeIfStale();

                var card = BuildCard();
                if (card is null)
                {
                    _client!.ClearPresence();
                    return;
                }

                Apply(card.Value.Card, card.Value.Since);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discord presence update failed");
        }
    }

    /// <summary>
    /// The card for the current state, or null when nothing should be shown at all (idle with the
    /// launcher card switched off). The elapsed timer only makes sense for a state that has a start.
    /// </summary>
    private (PresenceCard Card, DateTimeOffset? Since)? BuildCard()
    {
        switch (_presence.Current)
        {
            case GamePresence.InGame:
            {
                var fps = _monitor.Latest?.CurrentFps;
                return (PresenceComposer.Compose(GamePresence.InGame, fps, _badge, _profileName, _options),
                    _options.ShowElapsedTime ? _presence.InGameSince ?? DateTimeOffset.Now : null);
            }
            case GamePresence.Starting:
                return (PresenceComposer.Compose(GamePresence.Starting, null, _badge, _profileName, _options), null);
            default:
                if (_inLauncherEnabled && _launcherEverShown)
                {
                    return (PresenceComposer.Compose(GamePresence.NotRunning, null, _badge, null, _options),
                        _options.ShowElapsedTime ? _launcherVisibleSince : null);
                }
                return null;
        }
    }

    private void Apply(PresenceCard card, DateTimeOffset? since)
    {
        var smallKey = SmallImageKey(card);
        var smallText = smallKey.Length == 0 ? "" : card.SmallImageText;

        // The signature covers everything that reaches Discord, including the resolved art: resolving
        // it has to be able to re-push the card on its own. The library also dedupes, but this is
        // cheaper and it is what keeps a failed update from being retried forever.
        var signature = string.Join('|', LargeArtKey(), smallKey, since?.ToString("yyyyMMddHHmmss"), card.Signature);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastSignature = signature;
        _lastPush = DateTimeOffset.Now;

        _client!.SetPresence(new RichPresence
        {
            Details = card.Details,
            State = NullIfEmpty(card.State),
            DetailsUrl = NullIfEmpty(card.DetailsUrl),
            StatusDisplay = ToLibrary(card.StatusDisplay),
            Timestamps = since is { } at ? new Timestamps(at.UtcDateTime) : null,
            Assets = new Assets
            {
                LargeImageKey = LargeArtKey(),
                LargeImageText = NullIfEmpty(card.LargeImageText),
                LargeImageUrl = NullIfEmpty(card.LargeImageUrl),
                SmallImageKey = NullIfEmpty(smallKey),
                SmallImageText = NullIfEmpty(smallText),
            },
            Buttons = PresenceButtons,
        });
    }

    /// <summary>The app mark: the uploaded asset when the application has one, else the public artwork.</summary>
    private string LargeArtKey()
        => DiscordArtCatalog.Pick(_uploadedAssets, PresenceArtKey) ?? PresenceArtUrl;

    /// <summary>
    /// The rank emblem: the uploaded asset for this tier when the application has one, else the public
    /// artwork. Matching is strict on purpose - a missing rank icon must not fall back to some other
    /// upload, which would pin one tier's emblem onto every card.
    /// </summary>
    private string SmallImageKey(PresenceCard card)
    {
        if (DiscordRankArt.SlugForTier(card.RankTier) is not { } slug)
        {
            return "";
        }
        return DiscordArtCatalog.Match(_uploadedAssets, DiscordRankArt.UploadedKey(slug))
            ?? DiscordRankArt.PublicUrlForTier(card.RankTier);
    }

    /// <summary>
    /// Asks Discord which art this application really has. Runs once per application id, and is retried
    /// on the housekeeping tick while it has never succeeded: a lookup that failed because the network
    /// was briefly down must not leave the card on the fallback artwork forever.
    /// </summary>
    private void RefreshArtIfNeeded()
    {
        if (_artResolved || _artResolving)
        {
            return;
        }
        _artResolving = true;
        _ = ResolveArtAsync();
    }

    private async Task ResolveArtAsync()
    {
        try
        {
            var assets = await _artCatalog.FindAssetsAsync(_applicationId).ConfigureAwait(false);
            if (assets.Count == 0)
            {
                _logger.LogInformation(
                    "The Discord application has no art assets uploaded, so the presence card uses the " +
                    "public artwork; upload optima-presence.png in the developer portal to use the app's own mark");
                return;
            }

            _uploadedAssets = assets;
            _artResolved = true;
            _logger.LogInformation("Discord presence art resolved against {Count} uploaded asset(s)", assets.Count);
            UpdatePresence();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving the Discord presence art failed");
        }
        finally
        {
            // Left false on failure so the next tick tries again; true only once the answer is real.
            _artResolving = false;
        }
    }

    private void RefreshBadgeIfStale()
    {
        if (DateTimeOffset.Now - _badgeAt <= BadgeLifetime)
        {
            return;
        }
        _badgeAt = DateTimeOffset.Now;
        _badgeName = _playerIgn;
        _ = RefreshBadgeAsync(_playerIgn, _playerAccountId);
    }

    /// <summary>
    /// Reads the player's ranked record and rank for the card. Failure is not fatal: the card keeps
    /// whatever it had, and the next stale check tries again.
    /// </summary>
    private async Task RefreshBadgeAsync(string ign, long? accountId)
    {
        if (ign.Length == 0 && accountId is not > 0)
        {
            return;
        }

        try
        {
            var lookup = await _cops.LookupPlayerAsync(ign, accountId).ConfigureAwait(false);
            if (lookup.Profile is not { } profile || CopsApiClient.LooksLikePlaceholderProfile(profile))
            {
                _badge = null;
            }
            else if (profile.CurrentSeason is { } season)
            {
                // Rank and MMR are only meaningful together; a rating of 0 means nothing is placed yet.
                var ranked = profile.Mmr is > 0;
                _badge = new PlayerSeasonBadge(
                    profile.Name,
                    season.Ranked.Wins,
                    season.Ranked.Losses,
                    ranked ? CopsRankLadder.Label(profile.Rank, profile.Mmr) : string.Empty,
                    ranked ? CopsRankLadder.Resolve(profile.Rank, profile.Mmr)?.Tier : null,
                    ranked ? profile.Mmr : null);
            }
            else
            {
                _badge = null;
            }

            // The badge is what the card's rank line and emblem come from, so the card is re-pushed
            // once it lands. Without this the launcher card would keep the rankless shape until some
            // other event happened to refresh it.
            UpdatePresence();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Presence badge lookup failed");
        }
    }

    private bool TryEnsureClient()
    {
        if (_client is { IsDisposed: false })
        {
            return true;
        }
        if (_applicationId.Length == 0 || !ulong.TryParse(_applicationId, out _))
        {
            return false;
        }
        try
        {
            _client = new DiscordRpcClient(_applicationId)
            {
                SkipIdenticalPresence = true,
            };
            // The library runs its own reconnect loop with a backoff, so a Discord client that is not
            // running yet (or is restarted later) is recovered on its own; these events only explain
            // what is happening in the log.
            _client.OnConnectionFailed += (_, _) =>
                _logger.LogDebug("Discord is not running; presence retries in the background");
            _client.OnReady += (_, _) => _logger.LogDebug("Discord presence pipe is ready");
            _client.Initialize();
            _logger.LogInformation("Discord rich presence connected (app {Id})", _applicationId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discord presence client failed to start");
            _client = null;
            return false;
        }
    }

    private void SafeClear()
    {
        try
        {
            _client?.ClearPresence();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clearing Discord presence failed");
        }
    }

    private void TearDownClient()
    {
        try
        {
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the Discord client failed");
        }
        _client = null;
        _lastSignature = "";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static StatusDisplayType ToLibrary(PresenceStatusDisplay display) => display switch
    {
        PresenceStatusDisplay.State => StatusDisplayType.State,
        PresenceStatusDisplay.Details => StatusDisplayType.Details,
        _ => StatusDisplayType.Name,
    };

    public void Dispose()
    {
        if (_subscribed)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            _presence.PresenceChanged -= OnPresenceChanged;
            _monitor.MetricsUpdated -= OnMetrics;
            _subscribed = false;
        }
        _tick?.Dispose();
        _tick = null;
        SafeClear();
        TearDownClient();
    }
}
