using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Crash-safe restore pipeline (§18/§19).</summary>
public interface IRecoveryService
{
    Task SavePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task UpdatePendingAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task<SystemStateSnapshot?> GetPendingAsync(CancellationToken ct = default);

    Task RestoreAsync(SystemStateSnapshot snapshot, CancellationToken ct = default);

    Task ClearPendingAsync(CancellationToken ct = default);
}
