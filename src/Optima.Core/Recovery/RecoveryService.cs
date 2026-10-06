using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Recovery;

/// <summary>
/// Implements the crash-recovery contract (§18/§19): the snapshot is persisted before any mutation, updated as the
/// session progresses, and restored best-effort: every restore step runs even if an earlier one fails, and failures are
/// logged rather than thrown.
/// </summary>
public sealed class RecoveryService : IRecoveryService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly IDisplayService _display;
    private readonly IPowerProfileService _power;
    private readonly IVirtualDisplayProvider _virtualDisplay;
    private readonly IProcessOptimizer _processOptimizer;
    private readonly ILogger<RecoveryService> _logger;
    private readonly TimeSpan _retryDelay;

    /// <param name="retryDelay">How long a failed step waits before its one second try.</param>
    public RecoveryService(
        AppPaths paths,
        JsonStore store,
        IDisplayService display,
        IPowerProfileService power,
        IVirtualDisplayProvider virtualDisplay,
        IProcessOptimizer processOptimizer,
        ILogger<RecoveryService> logger,
        TimeSpan? retryDelay = null)
    {
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(400);
        _paths = paths;
        _store = store;
        _display = display;
        _power = power;
        _virtualDisplay = virtualDisplay;
        _processOptimizer = processOptimizer;
        _logger = logger;
    }

    public Task SavePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default)
    {
        _logger.LogInformation("Recovery snapshot persisted for profile {Profile}", snapshot.ProfileName);
        return _store.SaveAsync(_paths.PendingSnapshotFile, snapshot, ct);
    }

    public Task UpdatePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default)
        => _store.SaveAsync(_paths.PendingSnapshotFile, snapshot, ct);

    public Task<SystemStateSnapshot?> GetPendingAsync(CancellationToken ct = default)
        => _store.LoadAsync<SystemStateSnapshot>(_paths.PendingSnapshotFile, ct);

    public async Task<RestoreReport> RestoreAsync(SystemStateSnapshot snapshot, CancellationToken ct = default)
    {
        _logger.LogInformation("Restoring system state from snapshot created {CreatedAt:u}", snapshot.CreatedAt);

        var failed = new List<string>();
        // What is still owed if something fails: the snapshot with every step that went through
        // struck out. A process that has exited has nothing left to restore, so those never stay.
        var left = snapshot with { ProcessStates = [] };

        // Order matters: undo process tweaks first (cheap), then power, then display mode,
        // then topology, then take the virtual display down last so the desktop never ends
        // up parked on a display that is about to disappear.
        foreach (var proc in snapshot.ProcessStates)
        {
            await Attempt($"process settings for {proc.ProcessName} ({proc.ProcessId})",
                () => _processOptimizer.RestoreAsync(proc, ct), retry: false).ConfigureAwait(false);
        }

        if (snapshot.PreviousPowerScheme is { } scheme)
        {
            if (await Attempt("power plan", () => _power.RestoreAsync(scheme, ct)).ConfigureAwait(false))
            {
                left = left with { PreviousPowerScheme = null };
            }
            else
            {
                failed.Add("power plan");
            }
        }

        if (snapshot.ChangedDisplayDevice is { } device && snapshot.OriginalDisplayMode is { } mode)
        {
            if (await Attempt($"display mode on {device}", () => _display.ApplyModeAsync(device, mode, ct)).ConfigureAwait(false))
            {
                left = left with { ChangedDisplayDevice = null, OriginalDisplayMode = null };
            }
            else
            {
                failed.Add("display mode");
            }
        }

        if (snapshot.DisplayTopology is { } topology)
        {
            if (await Attempt("display topology", () => _display.RestoreTopologyAsync(topology, ct)).ConfigureAwait(false))
            {
                left = left with { DisplayTopology = null };
            }
            else
            {
                failed.Add("display topology");
            }
        }

        var virtualDisplayRestored = true;
        if (snapshot.VirtualDisplayEnabledByUs || snapshot.VirtualDisplayConfigured)
        {
            virtualDisplayRestored &= await Attempt("virtual display", () => _virtualDisplay.RestoreOriginalStateAsync(ct)).ConfigureAwait(false);
        }

        // The provider only switches the device off when it remembers enabling it, and after a crash
        // it is a new instance that remembers nothing. The snapshot does. A no-op when already off.
        if (snapshot.VirtualDisplayEnabledByUs)
        {
            virtualDisplayRestored &= await Attempt("virtual display device", () => _virtualDisplay.DisableDisplayAsync(ct)).ConfigureAwait(false);
        }
        if (virtualDisplayRestored)
        {
            left = left with { VirtualDisplayEnabledByUs = false, VirtualDisplayConfigured = false };
        }
        else
        {
            failed.Add("virtual display");
        }

        if (failed.Count == 0)
        {
            await ClearPendingAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Settings restored");
        }
        else
        {
            // "Settings restored" used to be logged here whatever had happened, and the snapshot
            // deleted with it, so a failed step could never be finished later.
            await _store.SaveAsync(_paths.PendingSnapshotFile, left, CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("Settings restored except {Failed}; that part stays pending so it can be finished",
                string.Join(", ", failed));
        }
        return new RestoreReport(failed);
    }

    public Task ClearPendingAsync(CancellationToken ct = default)
    {
        _store.Delete(_paths.PendingSnapshotFile);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One restore step, tried twice. Windows refuses a display or power change while another one
    /// is still settling, and the same call a moment later usually goes through.
    /// </summary>
    private async Task<bool> Attempt(string what, Func<Task> action, bool retry = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                _logger.LogInformation("Restored {What}", what);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (retry && attempt == 1)
                {
                    _logger.LogDebug(ex, "Restoring {What} failed, trying once more", what);
                    await Task.Delay(_retryDelay).ConfigureAwait(false);
                    continue;
                }
                _logger.LogError(ex, "Failed restoring {What}, continuing with remaining restore steps", what);
                return false;
            }
        }
    }
}
