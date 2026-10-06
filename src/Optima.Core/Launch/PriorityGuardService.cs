using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>What the guard is doing right now, for the BOOST page.</summary>
public sealed record PriorityGuardStatus(
    bool Enabled,
    ProcessPriorityLevel Wanted,
    int ProcessCount,
    bool SessionOwned,
    int Corrections,
    DateTimeOffset? GuardingSince,
    DateTimeOffset? LastCheck)
{
    public static PriorityGuardStatus Idle { get; } = new(false, ProcessPriorityLevel.Unchanged, 0, false, 0, null, null);
}

/// <summary>
/// Keeps the game's processes at the chosen priority for as long as they exist, whoever started
/// them. A session Optima launched already has a keeper of its own, bound to the one process it
/// started; this covers what that cannot: a game opened straight from Google Play Games, an
/// emulator that restarted, more than one emulator process, and a priority changed mid-game.
/// While an Optima session is active the guard stands down, so the two never write against each
/// other. What it raised it puts back when it is switched off or the app exits.
/// </summary>
public sealed class PriorityGuardService : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IProcessMonitor _monitor;
    private readonly IProcessOptimizer _optimizer;
    private readonly Func<AppSettings?> _settings;
    private readonly Func<bool> _sessionActive;
    private readonly ILogger<PriorityGuardService> _logger;
    private readonly TimeSpan _interval;

    private readonly Dictionary<int, ProcessStateSnapshot> _baselines = [];
    private readonly SemaphoreSlim _pass = new(1, 1);
    private CancellationTokenSource? _cts;
    private GamePresenceService? _presence;
    private DateTimeOffset? _since;
    private int _corrections;

    public PriorityGuardService(
        IProcessMonitor monitor,
        IProcessOptimizer optimizer,
        Func<AppSettings?> settings,
        Func<bool> sessionActive,
        ILogger<PriorityGuardService> logger,
        TimeSpan? interval = null)
    {
        _monitor = monitor;
        _optimizer = optimizer;
        _settings = settings;
        _sessionActive = sessionActive;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    public PriorityGuardStatus Status { get; private set; } = PriorityGuardStatus.Idle;

    /// <summary>Raised after every pass, on whatever thread ran it.</summary>
    public event Action<PriorityGuardStatus>? StatusChanged;

    /// <summary>Starts the periodic pass; a presence edge triggers one at once instead of waiting for the next.</summary>
    public void Start(GamePresenceService? presence = null)
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _presence = presence;
        if (presence is not null)
        {
            presence.PresenceChanged += OnPresenceChanged;
        }
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private void OnPresenceChanged(PresenceChange change) => _ = Task.Run(() => CheckNowAsync());

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await CheckNowAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass: find the game's processes, raise the new ones, correct the ones that drifted.</summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var enabled = settings?.BoostPriorityGuardEnabled ?? false;
            var wanted = Enum.TryParse<ProcessPriorityLevel>(settings?.GamePriority, ignoreCase: true, out var level)
                ? level
                : ProcessPriorityLevel.Unchanged;

            if (!enabled || wanted == ProcessPriorityLevel.Unchanged)
            {
                await ReleaseAsync(restore: true, ct).ConfigureAwait(false);
                Publish(enabled, wanted, sessionOwned: false);
                return;
            }

            if (_sessionActive())
            {
                // The session captured its own baseline and restores from it; restoring ours as well
                // would write the same process twice.
                await ReleaseAsync(restore: false, ct).ConfigureAwait(false);
                Publish(enabled, wanted, sessionOwned: true);
                return;
            }

            var tracked = await _monitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false);
            var pids = tracked.Where(p => p.Kind == TrackedProcessKind.Emulator).Select(p => p.ProcessId).ToHashSet();

            foreach (var gone in _baselines.Keys.Where(pid => !pids.Contains(pid)).ToList())
            {
                _baselines.Remove(gone);
            }

            var profile = new PerformanceProfile { Priority = wanted };
            foreach (var pid in pids)
            {
                if (_baselines.TryGetValue(pid, out var baseline))
                {
                    if (await _optimizer.ReassertAsync(baseline, profile, ct).ConfigureAwait(false))
                    {
                        _corrections++;
                    }
                }
                else if (await _optimizer.ApplyAsync(pid, profile, ct).ConfigureAwait(false) is { } snapshot)
                {
                    _baselines[pid] = snapshot;
                    _since ??= DateTimeOffset.Now;
                    _logger.LogInformation("Priority guard holds {Priority} on pid {Pid}", wanted, pid);
                }
            }

            if (_baselines.Count == 0)
            {
                _since = null;
                _corrections = 0;
            }
            Publish(enabled, wanted, sessionOwned: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Priority guard pass failed");
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task ReleaseAsync(bool restore, CancellationToken ct)
    {
        if (restore)
        {
            foreach (var baseline in _baselines.Values)
            {
                await _optimizer.RestoreAsync(baseline, ct).ConfigureAwait(false);
            }
        }
        _baselines.Clear();
        _since = null;
        _corrections = 0;
    }

    private void Publish(bool enabled, ProcessPriorityLevel wanted, bool sessionOwned)
    {
        Status = new PriorityGuardStatus(enabled, wanted, _baselines.Count, sessionOwned, _corrections, _since, DateTimeOffset.Now);
        StatusChanged?.Invoke(Status);
    }

    public void Dispose()
    {
        if (_presence is not null)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
        }
        _cts?.Cancel();
        try
        {
            // Bounded: app exit must not hang on a process that will not answer.
            Task.Run(async () =>
            {
                await _pass.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ReleaseAsync(restore: true, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _pass.Release();
                }
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Priority guard could not restore on exit");
        }
        _cts?.Dispose();
    }
}
