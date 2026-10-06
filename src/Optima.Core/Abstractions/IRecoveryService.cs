using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>What a restore could not put back. Empty when everything was.</summary>
public sealed record RestoreReport(IReadOnlyList<string> Failed)
{
    public bool AllRestored => Failed.Count == 0;
}

/// <summary>Crash-safe restore pipeline (§18/§19).</summary>
public interface IRecoveryService
{
    Task SavePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task UpdatePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task<SystemStateSnapshot?> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Puts back what the snapshot records. Every step runs even when an earlier one fails; what
    /// failed stays pending on disk, and only that, so it can be tried again.
    /// </summary>
    Task<RestoreReport> RestoreAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task ClearPendingAsync(CancellationToken ct = default);
}
