using System.Windows;
using DiscordRPC;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>
/// Discord Rich Presence, fed by the Watchdog's presence service. The card is composed from
/// live facts (per Discord's best practices): what is running, the player's ranked record,
/// a live fps readout during sessions, and the elapsed time. Updates are throttled while in
/// game so the fps line stays fresh without spamming the RPC pipe.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private static readonly TimeSpan FpsPushInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BadgeLifetime = TimeSpan.FromMinutes(30);

    private readonly GamePresenceService _presence;
    private readonly SettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly Optima.Core.Stats.CopsApiClient _cops;
    private readonly Optima.Core.Discord.DiscordArtCatalog _artCatalog;
    private readonly ILogger<DiscordPresenceService> _logger;

    private readonly object _gate = new();

    /// <summary>
    /// The uploaded asset the card prefers, named after the file it comes from: upload
    /// <c>Assets/optima-presence.png</c> in the Discord Developer Portal (Rich Presence -&gt; Art
    /// Assets) keeping that filename and the key is derived from it. What the application actually
    /// has is read from Discord rather than assumed, because a key that does not exist renders as a
    /// question mark with nothing logged.
    /// </summary>
    private const string PresenceArtKey = "optima-presence";

    /// <summary>
    /// Where the card points while the application has no asset uploaded. This is the artwork as the
    /// published repository serves it: an older mark than the one in the app, but a usable image rather
    /// than a broken one, and it stops being used the moment the upload exists.
    /// </summary>
    private const string PresenceArtUrl =
        "https://raw.githubusercontent.com/inspectsoftware/Optima/master/src/Optima.App/Assets/optima-presence.png";

    private static readonly Button[] PresenceButtons =
    [
        new Button { Label = "Join Discord", Url = "https://discord.gg/tktZe8fkmj" },
        new Button { Label = "Private Beta", Url = "https://github.com/inspectsoftware/Optima/releases" },
    ];

    private DiscordRpcClient? _client;
    private volatile bool _enabled;
    private volatile bool _inLauncherEnabled;
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

    // The presence card's large image: the public artwork until the application's own asset is known
    // to exist, and the uploaded key forever after.
    private volatile string _artKey = PresenceArtUrl;
    private bool _artResolved;

    private PlayerSeasonBadge? _badge;
    private DateTimeOffset _badgeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
    private string _lastSignature = "";

    public DiscordPresenceService(
        GamePresenceService presence,
        SettingsService settings,
        IPerformanceMonitor monitor,
        Optima.Core.Stats.CopsApiClient cops,
        Optima.Core.Discord.DiscordArtCatalog artCatalog,
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
            _settings.SettingsChanged += (_, s) => ApplySettings(s);
            _presence.PresenceChanged += OnPresenceChanged;
            _monitor.MetricsUpdated += OnMetrics;
            _subscribed = true;
        }
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

    private void ApplySettings(AppSettings settings)
    {
        _playerIgn = settings.PlayerIgn;
        _playerAccountId = settings.PlayerAccountId;
        _profileName = settings.SelectedProfileName;
        _enabled = settings.DiscordPresenceEnabled;
        _inLauncherEnabled = settings.DiscordPresenceInLauncher;
        var newId = settings.DiscordApplicationId.Trim();
        if (!string.Equals(newId, _applicationId, StringComparison.Ordinal))
        {
            _applicationId = newId;
            // A different application has its own assets, so the art is looked up again.
            _artResolved = false;
            _artKey = PresenceArtUrl;
            TearDownClient();
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

                if (!_artResolved)
                {
                    // Once per application, and never on this thread: the answer only changes when
                    // someone uploads to the portal, so there is nothing to poll for.
                    _artResolved = true;
                    _ = ResolveArtAsync();
                }

                switch (_presence.Current)
                {
                    case GamePresence.InGame:
                    {
                        var since = _presence.InGameSince ?? DateTimeOffset.Now;
                        SetComposed(ComposeCurrent(GamePresence.InGame), since);
                        break;
                    }
                    case GamePresence.Starting:
                    {
                        SetComposed(ComposeCurrent(GamePresence.Starting), null);
                        break;
                    }
                    default:
                        if (_inLauncherEnabled && _launcherEverShown)
                        {
                            SetComposed(PresenceComposer.Compose(GamePresence.NotRunning, null, null, null),
                                _launcherVisibleSince);
                        }
                        else
                        {
                            _client!.ClearPresence();
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discord presence update failed");
        }
    }

    private PresenceText ComposeCurrent(GamePresence state)
    {
        var fps = state == GamePresence.InGame ? _monitor.Latest?.CurrentFps : null;

        if (DateTimeOffset.Now - _badgeAt > BadgeLifetime)
        {
            _badgeAt = DateTimeOffset.Now;
            _ = RefreshBadgeAsync(_playerIgn, _playerAccountId);
        }

        return PresenceComposer.Compose(state, fps, _badge, _profileName);
    }

    private void SetComposed(PresenceText text, DateTimeOffset? since)
    {
        // Skip identical payloads; the library also dedupes, but the signature is cheaper. The art is
        // part of the payload: resolving it has to be able to re-push the card on its own.
        var signature = _artKey + "|" + text.Details + "|" + text.State + "|" + since?.ToString("yyyyMMddHHmmss");
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }
        _lastSignature = signature;
        _lastPush = DateTimeOffset.Now;

        _client!.SetPresence(new RichPresence
        {
            Details = text.Details,
            State = string.IsNullOrWhiteSpace(text.State) ? null : text.State,
            Timestamps = since is { } at ? new Timestamps(at.UtcDateTime) : null,
            Assets = new Assets
            {
                LargeImageKey = _artKey,
                LargeImageText = text.LargeImageText,
            },
            Buttons = PresenceButtons,
        });
    }

    /// <summary>
    /// Asks Discord which art this application really has. Nothing uploaded means the card keeps the
    /// public artwork instead of a question mark, and the answer is logged once so the state of the
    /// portal upload is visible in the log rather than in the rendered card.
    /// </summary>
    private async Task ResolveArtAsync()
    {
        try
        {
            var key = await _artCatalog.FindKeyAsync(_applicationId, PresenceArtKey).ConfigureAwait(false);
            if (key is null)
            {
                _logger.LogInformation(
                    "The Discord application has no art assets uploaded, so the presence card uses the " +
                    "public artwork URL; upload optima-presence.png in the developer portal to use the " +
                    "app's own mark");
                return;
            }

            if (!string.Equals(key, _artKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Discord presence art resolved to the uploaded asset '{Key}'", key);
                _artKey = key;
                UpdatePresence();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving the Discord presence art failed");
        }
    }

    private async Task RefreshBadgeAsync(string ign, long? accountId)
    {
        try
        {
            var lookup = await _cops.LookupPlayerAsync(ign, accountId).ConfigureAwait(false);
            if (lookup.Profile?.CurrentSeason is { } season)
            {
                _badge = new PlayerSeasonBadge(
                    lookup.Profile.Name,
                    season.Ranked.Wins,
                    season.Ranked.Losses);
            }
            else
            {
                _badge = null;
            }
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
            _client.OnConnectionFailed += (_, _) =>
                _logger.LogDebug("Discord is not running; presence stays quiet");
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
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
            _monitor.MetricsUpdated -= OnMetrics;
            _subscribed = false;
        }
        SafeClear();
        TearDownClient();
    }
}
