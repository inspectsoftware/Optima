using System.Diagnostics;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using COPSBootstrapper.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.Platform.Windows.Services;

/// <summary>
/// Reversible per-process tuning (§9): priority class, CPU affinity, EcoQoS power throttling.
/// Original values are captured first and returned so the recovery system can undo them.
/// </summary>
public sealed class WindowsProcessOptimizer : IProcessOptimizer
{
    private readonly ILogger<WindowsProcessOptimizer> _logger;

    public WindowsProcessOptimizer(ILogger<WindowsProcessOptimizer> logger)
    {
        _logger = logger;
    }

    public Task<ProcessStateSnapshot?> ApplyAsync(int processId, PerformanceProfile profile, CancellationToken ct = default)
        => Task.Run<ProcessStateSnapshot?>(() =>
        {
            var wantsPriority = profile.Priority != ProcessPriorityLevel.Unchanged;
            var wantsAffinity = profile.CpuAffinityMask != 0;
            if (!wantsPriority && !wantsAffinity && !profile.DisablePowerThrottling)
            {
                return null;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning("Process {Pid} exited before optimization could be applied", processId);
                return null;
            }

            using (process)
            {
                var snapshot = new ProcessStateSnapshot
                {
                    ProcessId = processId,
                    ProcessName = process.ProcessName,
                    OriginalPriority = FromPriorityClass(process.PriorityClass),
                    OriginalAffinityMask = (ulong)process.ProcessorAffinity.ToInt64(),
                    PowerThrottlingWasEnabled = ProcessNative.IsPowerThrottlingEnabled(process.Handle),
                };

                try
                {
                    if (wantsPriority)
                    {
                        process.PriorityClass = ToPriorityClass(profile.Priority);
                        _logger.LogInformation("Priority {Priority} applied to {Name} ({Pid})", profile.Priority, process.ProcessName, processId);
                    }

                    if (wantsAffinity)
                    {
                        var systemMask = (ulong)(Environment.ProcessorCount >= 64
                            ? ulong.MaxValue
                            : (1UL << Environment.ProcessorCount) - 1);
                        var mask = profile.CpuAffinityMask & systemMask;
                        if (mask != 0)
                        {
                            process.ProcessorAffinity = (nint)mask;
                            _logger.LogInformation("CPU affinity 0x{Mask:X} applied to {Name}", mask, process.ProcessName);
                        }
                    }

                    if (profile.DisablePowerThrottling)
                    {
                        ProcessNative.SetPowerThrottling(process.Handle, enabled: false);
                        _logger.LogInformation("Power throttling disabled for {Name}", process.ProcessName);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogWarning(ex, "Could not fully optimize process {Pid} — partial settings remain and will be restored", processId);
                }

                return snapshot;
            }
        }, ct);

    public Task RestoreAsync(ProcessStateSnapshot snapshot, CancellationToken ct = default)
        => Task.Run(() =>
        {
            Process process;
            try
            {
                process = Process.GetProcessById(snapshot.ProcessId);
            }
            catch (ArgumentException)
            {
                return; // Process already gone — nothing to restore.
            }

            using (process)
            {
                // Guard against PID reuse: only touch the process if the name still matches.
                if (!string.Equals(process.ProcessName, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    process.PriorityClass = ToPriorityClass(snapshot.OriginalPriority);
                    if (snapshot.OriginalAffinityMask != 0)
                    {
                        process.ProcessorAffinity = (nint)snapshot.OriginalAffinityMask;
                    }
                    // Return throttling control to the system (matches pre-session behavior).
                    ProcessNative.SetPowerThrottling(process.Handle, snapshot.PowerThrottlingWasEnabled ? true : null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogWarning(ex, "Could not restore process settings for {Pid}", snapshot.ProcessId);
                }
            }
        }, ct);

    private static ProcessPriorityClass ToPriorityClass(ProcessPriorityLevel level) => level switch
    {
        ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriorityLevel.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };

    private static ProcessPriorityLevel FromPriorityClass(ProcessPriorityClass priorityClass) => priorityClass switch
    {
        ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
        ProcessPriorityClass.High => ProcessPriorityLevel.High,
        _ => ProcessPriorityLevel.Normal,
    };
}
