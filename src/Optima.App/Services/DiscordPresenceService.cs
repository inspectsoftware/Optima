using System.Windows;
using DiscordRPC;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>Discord Rich Presence, fed by the Watchdog's presence service.</summary>
public sealed class DiscordPresenceService : IDisposable
{
    private readonly GamePresenceService _presence;
    private readonly SettingsService _settings;
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
    private DateTimeOffset _launcherVisibleSince = DateTimeOffset.Now;
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

    public void AttachLauncherWindow(Window window)
    {
        lock (_gate)
        {
            _launcherVisible = window.IsVisible;
            _launcherVisibleSince = DateTimeOffset.Now;
        }
        window.IsVisibleChanged += (_, args) =>
        {
            lock (_gate)
            {
                var visible = args.NewValue is true;
                if (visible && !_launcherVisible)
                {
                    _launcherVisibleSince = DateTimeOffset.Now;
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

    private void OnPresenceChanged(PresenceChange change) => UpdatePresence();

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
                        var since = _presence.InGameSince ?? DateTimeOffset.Now;
                        _client!.SetPresence(new RichPresence
                        {
                            Details = "Playing Critical Ops",
                            State = "In game",
                            Timestamps = new Timestamps(since.UtcDateTime),
                            Assets = new Assets
                            {
                                LargeImageKey = LargeImageUrl,
                                LargeImageText = "Optima",
                            },
                            Buttons = PresenceButtons,
                        });
                        break;
                    case GamePresence.Starting:
                        _client!.SetPresence(new RichPresence
                        {
                            Details = "Launching Critical Ops",
                            Assets = new Assets
                            {
                                LargeImageKey = LargeImageUrl,
                                LargeImageText = "Optima",
                            },
                            Buttons = PresenceButtons,
                        });
                        break;
                    default:
                        if (_inLauncherEnabled && _launcherVisible)
                        {
                            _client!.SetPresence(new RichPresence
                            {
                                Details = "Optima Launcher",
                                State = "Browsing the launcher",
                                Timestamps = new Timestamps(_launcherVisibleSince.UtcDateTime),
                                Assets = new Assets
                                {
                                    LargeImageKey = LargeImageUrl,
                                    LargeImageText = "Optima",
                                },
                                Buttons = PresenceButtons,
                            });
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
