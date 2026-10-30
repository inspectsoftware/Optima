using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>
/// Reads the Windows settings that sit under the game's frame rate but are not Optima's to change:
/// Memory Integrity, the hypervisor's scheduler and how the graphics card raises interrupts. Every
/// read is read-only, so the checks that use it can run unasked.
/// </summary>
public interface IHostTuningProbe
{
    Task<MemoryIntegrityReading> ReadMemoryIntegrityAsync(CancellationToken ct = default);

    /// <summary>
    /// The latest scheduler type the hypervisor logged as it started. Not only this boot's: with Fast
    /// Startup a start of Windows can resume the old one, and the type only changes with the boot
    /// settings anyway. Null when the System log holds none.
    /// </summary>
    Task<HypervisorSchedulerReading?> ReadHypervisorSchedulerAsync(CancellationToken ct = default);

    /// <summary>The hardware graphics adapters; software ones such as a virtual display raise no interrupts and are left out.</summary>
    Task<IReadOnlyList<GpuInterruptReading>> ReadGpuInterruptsAsync(CancellationToken ct = default);
}
