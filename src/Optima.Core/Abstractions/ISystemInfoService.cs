using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Static system facts (CPU/GPU/RAM/OS/displays/virtualization).</summary>
public interface ISystemInfoService
{
    Task<SystemInventory> GetInventoryAsync(CancellationToken ct = default);

    Task<VirtualizationState> GetVirtualizationStateAsync(CancellationToken ct = default);
}
