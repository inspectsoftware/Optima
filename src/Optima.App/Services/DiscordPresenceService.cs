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
    private readonly ILogger<DiscordPresenceService> _logger;

    private readonly object _gate = new();

    private const string LargeImageUrl =
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

    private PlayerSeasonBadge? _badge;
    private DateTimeOffset _badgeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
    private string _lastSignature = "";

    public DiscordPresenceService(
        GamePresenceService presence,
        SettingsService settings,
        IPerformanceMonitor monitor,
        Optima.Core.Stats.CopsApiClient cops,
        ILogger<DiscordPresenceService> logger)
    {
        _presence = presence;
        _settings = settings;
        _monitor = monitor;
        _cops = cops;
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
        _enabled = settings.DiscordPresenceEnabled;
        _inLauncherEnabled = settings.DiscordPresenceInLauncher;
        var newId = settings.DiscordApplicationId.Trim();
        if (!string.Equals(newId, _applicationId, StringComparison.Ordinal))
        {
            _applicationId = newId;
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
        var settings = _settings.GetSettingsAsync().GetAwaiter().GetResult();
        var fps = state == GamePresence.InGame ? _monitor.Latest?.CurrentFps : null;

        if (DateTimeOffset.Now - _badgeAt > BadgeLifetime)
        {
            _badgeAt = DateTimeOffset.Now;
            _ = RefreshBadgeAsync(settings.PlayerIgn, settings.PlayerAccountId);
        }

        return PresenceComposer.Compose(state, fps, _badge, settings.SelectedProfileName);
    }

    private void SetComposed(PresenceText text, DateTimeOffset? since)
    {
        // Skip identical payloads; the library also dedupes, but the signature is cheaper.
        var signature = text.Details + "|" + text.State + "|" + since?.ToString("yyyyMMddHHmmss");
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
                LargeImageKey = LargeImageUrl,
                LargeImageText = text.LargeImageText,
            },
            Buttons = PresenceButtons,
        });
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
