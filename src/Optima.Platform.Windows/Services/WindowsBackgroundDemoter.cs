using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Below-normal priority plus efficiency mode for the listed background programs. Nothing is closed.</summary>
public sealed class WindowsBackgroundDemoter : IBackgroundDemoter
{
    private readonly ILogger<WindowsBackgroundDemoter> _logger;

    public WindowsBackgroundDemoter(ILogger<WindowsBackgroundDemoter> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<ProcessStateSnapshot>> DemoteAsync(
        IReadOnlyList<string> processNames, IReadOnlySet<int> alreadyDemoted, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<ProcessStateSnapshot>>(() =>
        {
            var demoted = new List<ProcessStateSnapshot>();
            using var self = Process.GetCurrentProcess();
            var names = processNames
                .Select(NormalizeName)
                .Where(name => name.Length > 0 && !WindowsBackgroundCleanupService.IsProtected(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // One listing of the system for the whole list. Asking by name listed every process
            // once per name, and this runs every twenty seconds beside the game.
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (ct.IsCancellationRequested)
                    {
                        continue;
                    }
                    var name = string.Empty;
                    try
                    {
                        name = process.ProcessName;
                        if (!names.Contains(name))
                        {
                            continue;
                        }
                        // Another user's programs are not this user's to slow down.
                        if (alreadyDemoted.Contains(process.Id) || process.SessionId != self.SessionId)
                        {
                            continue;
                        }
                        var original = process.PriorityClass;
                        // Already out of the way, or raised on purpose by something that knows why.
                        if (original is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.RealTime)
                        {
                            continue;
                        }
                        var snapshot = new ProcessStateSnapshot
                        {
                            ProcessId = process.Id,
                            ProcessName = name,
                            OriginalPriority = WindowsProcessOptimizer.FromPriorityClass(original),
                            OriginalAffinityMask = 0,
                            PowerThrottlingWasEnabled = ProcessNative.IsPowerThrottlingEnabled(process.Handle),
                        };
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                        // Recorded before the second step: a priority lowered here has to be put
                        // back even when efficiency mode then fails.
                        demoted.Add(snapshot);
                        ProcessNative.SetPowerThrottling(process.Handle, enabled: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Exited, or protected: left exactly as it is.
                        _logger.LogDebug(ex, "Could not demote {Name}", name);
                    }
                }
            }

            if (demoted.Count > 0)
            {
                _logger.LogInformation("Demoted {Count} background process(es): {Names}",
                    demoted.Count, string.Join(", ", demoted.Select(d => d.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            return demoted;
        }, ct);

    /// <summary>"Chrome.exe " and "chrome" are the same entry.</summary>
    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
