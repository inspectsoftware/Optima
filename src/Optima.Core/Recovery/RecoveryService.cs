using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Recovery;

/// <summary>
/// Implements the crash-recovery contract (§18/§19): the snapshot is persisted before any
/// mutation, updated as the session progresses, and restored best-effort — every restore step
/// runs even if an earlier one fails, and failures are logged rather than thrown.
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

    public RecoveryService(
        AppPaths paths,
        JsonStore store,
        IDisplayService display,
        IPowerProfileService power,
        IVirtualDisplayProvider virtualDisplay,
        IProcessOptimizer processOptimizer,
        ILogger<RecoveryService> logger)
    {
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

    public async Task RestoreAsync(SystemStateSnapshot snapshot, CancellationToken ct = default)
    {
        _logger.LogInformation("Restoring system state from snapshot created {CreatedAt:u}", snapshot.CreatedAt);

        // Order matters: undo process tweaks first (cheap), then power, then display mode,
        // then topology, then take the virtual display down last so the desktop never ends
        // up parked on a display that is about to disappear.
        foreach (var proc in snapshot.ProcessStates)
        {
            await Attempt($"process settings for {proc.ProcessName} ({proc.ProcessId})",
                () => _processOptimizer.RestoreAsync(proc, ct)).ConfigureAwait(false);
        }

        if (snapshot.PreviousPowerScheme is { } scheme)
        {
            await Attempt("power plan", () => _power.RestoreAsync(scheme, ct)).ConfigureAwait(false);
        }

        if (snapshot.ChangedDisplayDevice is { } device && snapshot.OriginalDisplayMode is { } mode)
        {
            await Attempt($"display mode on {device}", () => _display.ApplyModeAsync(device, mode, ct)).ConfigureAwait(false);
        }

        if (snapshot.DisplayTopology is { } topology)
        {
            await Attempt("display topology", () => _display.RestoreTopologyAsync(topology, ct)).ConfigureAwait(false);
        }

        if (snapshot.VirtualDisplayEnabledByUs || snapshot.VirtualDisplayConfigured)
        {
            // The provider undoes everything it did: driver settings file, device state,
            // including after a crash (it keeps its own on-disk pending marker).
            await Attempt("virtual display", () => _virtualDisplay.RestoreOriginalStateAsync(ct)).ConfigureAwait(false);
        }

        await ClearPendingAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Settings restored");
    }

    public Task ClearPendingAsync(CancellationToken ct = default)
    {
        _store.Delete(_paths.PendingSnapshotFile);
        return Task.CompletedTask;
    }

    private async Task Attempt(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            _logger.LogInformation("Restored {What}", what);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed restoring {What} — continuing with remaining restore steps", what);
        }
    }
}
