using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>
/// Applies the session-scoped tweaks when a game session starts and restores the captured
/// originals when it ends, including on crashes and app exit. Only CurrentUser values are
/// touched here; the persistent catalog on PERFORMANCE still owns anything machine-wide.
/// </summary>
public sealed class SessionTweakService : IDisposable
{
    private readonly ITweakService _tweaks;
    private readonly GamePresenceService _presence;
    private readonly ILogger<SessionTweakService> _logger;

    private readonly List<string> _activeIds = [];
    private readonly object _gate = new();
    private bool _subscribed;

    public SessionTweakService(ITweakService tweaks, GamePresenceService presence, ILogger<SessionTweakService> logger)
    {
        _tweaks = tweaks;
        _presence = presence;
        _logger = logger;
    }

    /// <summary>Sets which session tweaks are wanted; effective from the next session start.</summary>
    public IReadOnlyList<string> EnabledIds { get; set; } = [];

    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (_subscribed)
        {
            return;
        }
        _presence.PresenceChanged += OnPresenceChanged;
        _presence.GameExited += OnGameExited;
        _subscribed = true;
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if (change.Current == GamePresence.InGame && change.Previous != GamePresence.InGame)
        {
            _ = Task.Run(() => ApplyAsync());
        }
    }

    private void OnGameExited(GameExit exit) => _ = Task.Run(() => RestoreAsync("session ended"));

    private async Task ApplyAsync()
    {
        List<string> wanted;
        lock (_gate)
        {
            wanted = [.. EnabledIds];
            _activeIds.Clear();
        }
        if (wanted.Count == 0)
        {
            return;
        }

        foreach (var id in wanted)
        {
            var definition = SessionTweakCatalog.Find(id);
            if (definition is null || definition.RequiresElevation)
            {
                continue;
            }
            try
            {
                var state = await _tweaks.SetEnabledAsync(id, enable: true).ConfigureAwait(false);
                if (state.Status == TweakStatus.Enabled)
                {
                    lock (_gate)
                    {
                        _activeIds.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session tweak {Id} failed to apply", id);
            }
        }

        StatusChanged?.Invoke(_activeIds.Count > 0
            ? "session tweaks applied: " + string.Join(", ", _activeIds)
            : string.Empty);
    }

    private async Task RestoreAsync(string reason)
    {
        string[] active;
        lock (_gate)
        {
            active = [.. _activeIds];
            _activeIds.Clear();
        }
        if (active.Length == 0)
        {
            return;
        }

        foreach (var id in active)
        {
            try
            {
                await _tweaks.SetEnabledAsync(id, enable: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session tweak {Id} failed to restore", id);
            }
        }
        StatusChanged?.Invoke($"session tweaks restored ({reason})");
    }

    public async Task DisposeAsync()
    {
        await RestoreAsync("shutdown").ConfigureAwait(false);
        if (_subscribed)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
            _presence.GameExited -= OnGameExited;
            _subscribed = false;
        }
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }
}
