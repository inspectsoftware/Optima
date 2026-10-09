using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Reversible per-process tuning (§9): priority class, CPU affinity, EcoQoS power throttling.</summary>
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
                ProcessStateSnapshot snapshot;
                try
                {
                    snapshot = new ProcessStateSnapshot
                    {
                        ProcessId = processId,
                        ProcessName = process.ProcessName,
                        OriginalPriority = FromPriorityClass(process.PriorityClass),
                        OriginalAffinityMask = (ulong)process.ProcessorAffinity.ToInt64(),
                        PowerThrottlingWasEnabled = ProcessNative.IsPowerThrottlingEnabled(process.Handle),
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Reading the current values is refused for a protected process and fails for one
                    // that just exited. Neither is a reason to fail a session that is already running.
                    _logger.LogWarning(ex, "Could not read process settings for {Pid}; leaving it untouched", processId);
                    return null;
                }

                try
                {
                    if (wantsPriority)
                    {
                        process.PriorityClass = ToPriorityClass(profile.Priority);
                        _logger.LogInformation("Priority {Priority} applied to {Name} ({Pid})", profile.Priority, process.ProcessName, processId);
                    }

                    if (wantsAffinity)
                    {
                        var mask = profile.CpuAffinityMask & SystemAffinityMask;
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
                    _logger.LogWarning(ex, "Could not fully optimize process {Pid}, partial settings remain and will be restored", processId);
                }

                return snapshot;
            }
        }, ct);

    public Task<bool> ReassertAsync(ProcessStateSnapshot baseline, PerformanceProfile profile, CancellationToken ct = default)
        => Task.Run(() =>
        {
            Process process;
            try
            {
                process = Process.GetProcessById(baseline.ProcessId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            using (process)
            {
                var changed = false;
                try
                {
                    // Guard against PID reuse: only touch the process if the name still matches.
                    if (!string.Equals(process.ProcessName, baseline.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (profile.Priority != ProcessPriorityLevel.Unchanged
                        && process.PriorityClass != ToPriorityClass(profile.Priority))
                    {
                        process.PriorityClass = ToPriorityClass(profile.Priority);
                        _logger.LogInformation("Priority {Priority} re-asserted on {Name} ({Pid})",
                            profile.Priority, process.ProcessName, baseline.ProcessId);
                        changed = true;
                    }

                    if (profile.CpuAffinityMask != 0)
                    {
                        var mask = profile.CpuAffinityMask & SystemAffinityMask;
                        if (mask != 0 && (ulong)process.ProcessorAffinity.ToInt64() != mask)
                        {
                            process.ProcessorAffinity = (nint)mask;
                            _logger.LogInformation("CPU affinity 0x{Mask:X} re-asserted on {Name}", mask, process.ProcessName);
                            changed = true;
                        }
                    }

                    if (profile.DisablePowerThrottling && ProcessNative.IsPowerThrottlingEnabled(process.Handle))
                    {
                        ProcessNative.SetPowerThrottling(process.Handle, enabled: false);
                        _logger.LogInformation("Power throttling disabled again for {Name}", process.ProcessName);
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Could not re-assert process settings for {Pid}", baseline.ProcessId);
                }
                return changed;
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
                return;
            }

            using (process)
            {
                try
                {
                    // Guard against PID reuse: only touch the process if the name still matches.
                    // Inside the try: a process that exits right here throws on the name read, and
                    // that must not end the restore of every process after it.
                    if (!string.Equals(process.ProcessName, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    process.PriorityClass = ToPriorityClass(snapshot.OriginalPriority);
                    if (snapshot.OriginalAffinityMask != 0)
                    {
                        process.ProcessorAffinity = (nint)snapshot.OriginalAffinityMask;
                    }
                    ProcessNative.SetPowerThrottling(process.Handle, snapshot.PowerThrottlingWasEnabled ? true : null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogWarning(ex, "Could not restore process settings for {Pid}", snapshot.ProcessId);
                }
            }
        }, ct);

    private static ulong SystemAffinityMask => Environment.ProcessorCount >= 64
        ? ulong.MaxValue
        : (1UL << Environment.ProcessorCount) - 1;

    internal static ProcessPriorityClass ToPriorityClass(ProcessPriorityLevel level) => level switch
    {
        ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriorityLevel.High => ProcessPriorityClass.High,
        ProcessPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriorityLevel.Idle => ProcessPriorityClass.Idle,
        _ => ProcessPriorityClass.Normal,
    };

    internal static ProcessPriorityLevel FromPriorityClass(ProcessPriorityClass priorityClass) => priorityClass switch
    {
        ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
        // Realtime is never handed back: restoring a process to it needs a privilege Optima does not hold.
        ProcessPriorityClass.High or ProcessPriorityClass.RealTime => ProcessPriorityLevel.High,
        ProcessPriorityClass.BelowNormal => ProcessPriorityLevel.BelowNormal,
        ProcessPriorityClass.Idle => ProcessPriorityLevel.Idle,
        _ => ProcessPriorityLevel.Normal,
    };
}
