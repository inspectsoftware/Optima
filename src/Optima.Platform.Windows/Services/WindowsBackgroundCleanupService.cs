using System.Diagnostics;
using Optima.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Closes only processes the user explicitly listed in their profile (§10).</summary>
public sealed class WindowsBackgroundCleanupService : IBackgroundCleanupService
{
    private static readonly HashSet<string> NeverTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "lsass", "svchost", "smss",
        "System", "Idle", "GooglePlayGamesServices", "crosvm", "client", "Bootstrapper",
        "Optima", "Optima.Watchdog",
    };

    private readonly ILogger<WindowsBackgroundCleanupService> _logger;

    public WindowsBackgroundCleanupService(ILogger<WindowsBackgroundCleanupService> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, ulong>> EstimateImpactAsync(IReadOnlyList<string> processNames, CancellationToken ct = default)
        => Task.Run<IReadOnlyDictionary<string, ulong>>(() =>
        {
            var impact = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Sanitize(processNames))
            {
                ulong bytes = 0;
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        bytes += (ulong)process.WorkingSet64;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                if (bytes > 0)
                {
                    impact[name] = bytes;
                }
            }
            return impact;
        }, ct);

    public async Task<IReadOnlyList<string>> CloseAsync(IReadOnlyList<string> processNames, CancellationToken ct = default)
    {
        var closed = new List<string>();
        foreach (var name in Sanitize(processNames))
        {
            ct.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    if (process.CloseMainWindow())
                    {
                        // Give it a moment to shut down cleanly before escalating.
                        if (!await Task.Run(() => process.WaitForExit(3000), ct).ConfigureAwait(false))
                        {
                            process.Kill(entireProcessTree: false);
                        }
                    }
                    else
                    {
                        process.Kill(entireProcessTree: false);
                    }
                    _logger.LogInformation("Closed background process {Name} ({Pid})", name, pid);
                    if (!closed.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        closed.Add(name);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    _logger.LogWarning(ex, "Could not close {Name}", name);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return closed;
    }

    private static IEnumerable<string> Sanitize(IReadOnlyList<string> names)
        => names
            .Select(n => n.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Trim()[..^4] : n.Trim())
            .Where(n => n.Length > 0 && !NeverTouch.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
