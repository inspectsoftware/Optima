using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Detection;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// The kill switch: terminates every process matching the emulator patterns (the Android VM
/// hosting the game), tree included, without confirmation or grace. Deliberately not routed
/// through <see cref="WindowsBackgroundCleanupService"/>: its never-touch list exists to keep
/// the *cleanup* feature away from these processes, while killing the game is an explicit
/// user action targeting exactly them.
/// </summary>
public sealed class WindowsGameTerminator : IGameTerminator
{
    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;
    private readonly ILogger<WindowsGameTerminator> _logger;

    public WindowsGameTerminator(
        Func<CancellationToken, Task<DetectionRules>> rulesProvider,
        ILogger<WindowsGameTerminator> logger)
    {
        _rulesProvider = rulesProvider;
        _logger = logger;
    }

    public async Task<GameKillResult> KillGameAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var killed = new List<string>();
            var failed = 0;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!GameDetectionEngine.MatchesAny(process.ProcessName, rules.EmulatorProcessPatterns))
                    {
                        continue;
                    }
                    var label = $"{process.ProcessName} (pid {process.Id})";
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    killed.Add(label);
                    _logger.LogInformation("Killed game process tree {Process}", label);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                    or System.ComponentModel.Win32Exception)
                {
                    failed++;
                    _logger.LogWarning(ex, "Could not kill process {Name}", process.ProcessName);
                }
                finally
                {
                    process.Dispose();
                }
            }

            return killed.Count > 0
                ? new GameKillResult(true, $"killed {string.Join(", ", killed)}")
                : new GameKillResult(false, failed > 0 ? "kill failed. See Logs." : "game not running");
        }, ct).ConfigureAwait(false);
    }
}
