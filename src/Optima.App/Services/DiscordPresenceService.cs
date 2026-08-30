using DiscordRPC;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>
/// Discord Rich Presence, fed by the Watchdog's presence service. Local IPC to the user's
/// running Discord client only: no bot, no server, no account access. Active only while
/// the game is starting or running, and only when a Discord Application ID is configured
/// and the toggle is on; everything degrades silently when Discord is not running.
/// Live ranked-vs-casual detail is intentionally absent this phase (no passive signal
/// exists during a match); the session's mode lands in history via the stats delta.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private readonly GamePresenceService _presence;
    private readonly SettingsService _settings;
    private readonly ILogger<DiscordPresenceService> _logger;

    private DiscordRpcClient? _client;
    private volatile bool _enabled;
    private string _applicationId = "";
    private bool _subscribed;

    public DiscordPresenceService(
        GamePresenceService presence,
        SettingsService settings,
        ILogger<DiscordPresenceService> logger)
    {
        _presence = presence;
        _settings = settings;
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
            _subscribed = true;
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _enabled = settings.DiscordPresenceEnabled;
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
            // Re-assert current state under the new settings.
            OnPresenceChanged(new PresenceChange(_presence.Current, _presence.Current, DateTimeOffset.Now));
        }
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        try
        {
            if (!_enabled || !TryEnsureClient())
            {
                return;
            }

            switch (change.Current)
            {
                case GamePresence.InGame:
                    var since = _presence.InGameSince ?? DateTimeOffset.Now;
                    _client!.SetPresence(new RichPresence
                    {
                        Details = "Playing Critical Ops",
                        State = "In game",
                        Timestamps = new Timestamps(since.UtcDateTime),
                        Assets = new Assets
                        {
                            LargeImageKey = "optima",
                            LargeImageText = "Optima by Aureum",
                        },
                    });
                    break;
                case GamePresence.Starting:
                    _client!.SetPresence(new RichPresence
                    {
                        Details = "Launching Critical Ops",
                        Assets = new Assets
                        {
                            LargeImageKey = "optima",
                            LargeImageText = "Optima by Aureum",
                        },
                    });
                    break;
                default:
                    _client!.ClearPresence();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discord presence update failed");
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
            _subscribed = false;
        }
        SafeClear();
        TearDownClient();
    }
}
