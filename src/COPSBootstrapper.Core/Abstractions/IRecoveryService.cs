using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Abstractions;

/// <summary>
/// Crash-safe restore pipeline (§18/§19). A snapshot is persisted *before* any system change;
/// it is deleted only after a successful restore.
/// </summary>
public interface IRecoveryService
{
    /// <summary>Persists the snapshot to disk before changes are applied.</summary>
    Task SavePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Updates the persisted snapshot as further changes are made mid-session.</summary>
    Task UpdatePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Snapshot left behind by a previous crashed session, if any.</summary>
    Task<SystemStateSnapshot?> GetPendingAsync(CancellationToken ct = default);

    /// <summary>Restores everything recorded in the snapshot (best-effort, logs each step), then clears it.</summary>
    Task RestoreAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Clears the pending snapshot after a clean restore.</summary>
    Task ClearPendingAsync(CancellationToken ct = default);
}
