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
    // One apply or restore at a time. A restore that ran beside a slow apply found nothing
    // recorded yet, returned, and the tweak the apply then recorded stayed on.
    private readonly SemaphoreSlim _serial = new(1, 1);
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
        _ = Task.Run(RestoreLeftoverAsync);
    }

    /// <summary>
    /// What a run that never got to its exit left switched on: Optima killed or crashed with the
    /// game open, or the PC lost power. The originals are still on record from that run, and that
    /// record is the only thing that marks a value as changed by Optima and not by the player.
    /// </summary>
    private async Task RestoreLeftoverAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var captured = await _tweaks.GetCapturedIdsAsync().ConfigureAwait(false);
            foreach (var id in captured)
            {
                bool active;
                lock (_gate)
                {
                    active = _activeIds.Contains(id);
                }
                if (active || SessionTweakCatalog.Find(id) is null)
                {
                    continue;
                }
                await _tweaks.SetEnabledAsync(id, enable: false).ConfigureAwait(false);
                _logger.LogInformation("Session tweak {Id} was left on by an earlier run and has been put back", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not put back the session tweaks left by an earlier run");
        }
        finally
        {
            _serial.Release();
        }
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
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            List<string> wanted;
            lock (_gate)
            {
                wanted = [.. EnabledIds];
            }

            var applied = new List<string>();
            foreach (var id in wanted)
            {
                var definition = SessionTweakCatalog.Find(id);
                // An entry with no values changes nothing, and must not be reported as applied.
                if (definition is null || definition.RequiresElevation || definition.Values.Count == 0)
                {
                    continue;
                }
                // Recorded before the first write, not after a full success: a tweak that got
                // half way, or threw after writing, still has to be put back at the end.
                lock (_gate)
                {
                    if (!_activeIds.Contains(id))
                    {
                        _activeIds.Add(id);
                    }
                }
                try
                {
                    var state = await _tweaks.SetEnabledAsync(id, enable: true).ConfigureAwait(false);
                    if (state.Status == TweakStatus.Enabled)
                    {
                        applied.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session tweak {Id} failed to apply", id);
                }
            }

            if (wanted.Count > 0)
            {
                StatusChanged?.Invoke(applied.Count > 0
                    ? "session tweaks applied: " + string.Join(", ", applied)
                    : string.Empty);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task RestoreAsync(string reason)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
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
        finally
        {
            _serial.Release();
        }
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
        try
        {
            // Waited for, or the process is gone before the first value is written back. Bounded:
            // app exit must not hang on a tweaks file that will not open.
            Task.Run(DisposeAsync).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Session tweaks could not be restored on exit");
        }
    }
}
