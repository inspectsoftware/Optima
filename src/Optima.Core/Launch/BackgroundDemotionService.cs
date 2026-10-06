using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

/// <summary>What the demotion is doing right now, for the BOOST page.</summary>
public sealed record BackgroundDemotionStatus(bool Enabled, bool GameOnScreen, int ProcessCount, int ProgramCount);

/// <summary>
/// While the game is on screen, keeps the listed background programs demoted, including the ones
/// that start mid-game (browsers spawn processes constantly). When the game leaves, when the
/// switch is turned off, and when Optima closes, every process gets back exactly what it had.
/// What was demoted is also written to disk, so if Optima itself dies mid-game the next start
/// still puts those processes back.
/// </summary>
public sealed class BackgroundDemotionService : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Heavy, common, and safe to slow down. Voice and music apps are left out on purpose: a
    /// demoted Discord is a stuttering voice call.
    /// </summary>
    public static IReadOnlyList<string> DefaultProcessNames { get; } =
    [
        "chrome", "msedge", "firefox", "opera", "brave",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices",
        "OneDrive", "GoogleDriveFS", "Dropbox",
    ];

    private readonly IBackgroundDemoter _demoter;
    private readonly IProcessOptimizer _optimizer;
    private readonly GamePresenceService _presence;
    private readonly Func<AppSettings?> _settings;
    private readonly JsonStore _store;
    private readonly string _ledgerPath;
    private readonly ILogger<BackgroundDemotionService> _logger;
    private readonly TimeSpan _interval;

    private readonly Dictionary<int, ProcessStateSnapshot> _demoted = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public BackgroundDemotionService(
        IBackgroundDemoter demoter,
        IProcessOptimizer optimizer,
        GamePresenceService presence,
        Func<AppSettings?> settings,
        JsonStore store,
        string ledgerPath,
        ILogger<BackgroundDemotionService> logger,
        TimeSpan? interval = null)
    {
        _demoter = demoter;
        _optimizer = optimizer;
        _presence = presence;
        _settings = settings;
        _store = store;
        _ledgerPath = ledgerPath;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    public BackgroundDemotionStatus Status { get; private set; } = new(false, false, 0, 0);

    public event Action<BackgroundDemotionStatus>? StatusChanged;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _presence.PresenceChanged += OnPresenceChanged;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if ((change.Current == GamePresence.InGame) != (change.Previous == GamePresence.InGame))
        {
            _ = Task.Run(() => SyncAsync());
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RestoreLeftoversAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await SyncAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Processes a previous run demoted and never got to restore (it crashed, or was killed).</summary>
    public async Task RestoreLeftoversAsync(CancellationToken ct = default)
    {
        try
        {
            var leftovers = await _store.LoadAsync<List<ProcessStateSnapshot>>(_ledgerPath, ct).ConfigureAwait(false);
            if (leftovers is not { Count: > 0 })
            {
                return;
            }
            // RestoreAsync checks the name behind each pid, so a pid Windows has reused is left alone.
            foreach (var snapshot in leftovers)
            {
                await _optimizer.RestoreAsync(snapshot, ct).ConfigureAwait(false);
            }
            _store.Delete(_ledgerPath);
            _logger.LogInformation("Restored {Count} background process(es) left demoted by an earlier run", leftovers.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore background processes left by an earlier run");
        }
    }

    /// <summary>One pass: demote what is new while the game is on screen, restore everything otherwise.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var enabled = settings?.BoostDemoteBackgroundEnabled ?? false;
            var inGame = _presence.Current == GamePresence.InGame;

            if (enabled && inGame)
            {
                var names = settings!.BoostDemoteProcessNames;
                var fresh = await _demoter.DemoteAsync(names, _demoted.Keys.ToHashSet(), ct).ConfigureAwait(false);
                foreach (var snapshot in fresh)
                {
                    _demoted[snapshot.ProcessId] = snapshot;
                }
                if (fresh.Count > 0)
                {
                    await _store.SaveAsync(_ledgerPath, _demoted.Values.ToList(), ct).ConfigureAwait(false);
                }
            }
            else
            {
                await RestoreAllAsync(ct).ConfigureAwait(false);
            }

            Status = new BackgroundDemotionStatus(
                enabled,
                inGame,
                _demoted.Count,
                _demoted.Values.Select(d => d.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Background demotion pass failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreAllAsync(CancellationToken ct)
    {
        if (_demoted.Count == 0)
        {
            return;
        }
        foreach (var snapshot in _demoted.Values)
        {
            await _optimizer.RestoreAsync(snapshot, ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Restored {Count} demoted background process(es)", _demoted.Count);
        _demoted.Clear();
        _store.Delete(_ledgerPath);
    }

    public void Dispose()
    {
        _presence.PresenceChanged -= OnPresenceChanged;
        _cts?.Cancel();
        try
        {
            // Bounded: app exit must not hang on a process that will not answer. Whatever does not
            // make it stays in the ledger and is restored by the next start.
            Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Background demotion could not restore on exit");
        }
        _cts?.Dispose();
    }
}
