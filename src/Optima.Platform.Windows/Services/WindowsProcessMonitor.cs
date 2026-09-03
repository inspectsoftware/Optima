using System.Text.RegularExpressions;
using Optima.Core.Abstractions;
using Optima.Core.Detection;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Watches Google Play Games / emulator / game processes by polling (§9).</summary>
public sealed class WindowsProcessMonitor : IProcessMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int ExitConfirmationPolls = 5; // window must stay gone this many polls to count as exit

    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;
    private readonly ILogger<WindowsProcessMonitor> _logger;

    public WindowsProcessMonitor(Func<CancellationToken, Task<DetectionRules>> rulesProvider, ILogger<WindowsProcessMonitor> logger)
    {
        _rulesProvider = rulesProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TrackedProcess>> GetTrackedProcessesAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<TrackedProcess>>(() =>
        {
            var windowsByPid = WindowNative.GetVisibleWindows()
                .GroupBy(w => w.ProcessId)
                .ToDictionary(g => g.Key, g => g.First().Title);

            var tracked = new List<TrackedProcess>();
            foreach (var (id, name) in ProcessSnapshot.GetRunning())
            {
                var kind = Classify(name, windowsByPid.GetValueOrDefault(id), rules);
                if (kind == TrackedProcessKind.Other)
                {
                    continue;
                }

                DateTimeOffset? started = null;
                using (var handle = ProcessQuery.Open(id))
                {
                    if (handle is not null)
                    {
                        started = ProcessQuery.GetStartTime(handle);
                    }
                }

                tracked.Add(new TrackedProcess
                {
                    ProcessId = id,
                    Name = name,
                    MainWindowTitle = windowsByPid.GetValueOrDefault(id, string.Empty),
                    Kind = kind,
                    StartedAt = started,
                });
            }
            return tracked;
        }, ct).ConfigureAwait(false);
    }

    public async Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var emulatorRunning = AnyProcessMatches(rules.EmulatorProcessPatterns);
            var windowPresent = GameWindowPresent(rules);
            return (emulatorRunning, windowPresent) switch
            {
                (true, true) => GameRuntimeState.Running,
                (true, false) => GameRuntimeState.Starting,
                _ => GameRuntimeState.NotRunning,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var emulatorPid = FirstProcessMatching(rules.EmulatorProcessPatterns);
            if (emulatorPid is not null && GameWindowPresent(rules))
            {
                return emulatorPid;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task WaitForGameExitAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var absentPolls = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var emulatorAlive = AnyProcessMatches(rules.EmulatorProcessPatterns);
            if (!emulatorAlive)
            {
                _logger.LogInformation("Game exited (emulator process ended)");
                return;
            }

            absentPolls = GameWindowPresent(rules) ? 0 : absentPolls + 1;
            if (absentPolls >= ExitConfirmationPolls)
            {
                _logger.LogInformation("Game exited (game window closed)");
                return;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static TrackedProcessKind Classify(string processName, string? windowTitle, DetectionRules rules)
    {
        if (GameDetectionEngine.MatchesAny(processName, rules.EmulatorProcessPatterns))
        {
            return TrackedProcessKind.Emulator;
        }
        if (GameDetectionEngine.MatchesAny(processName, rules.PlatformProcessPatterns))
        {
            return TrackedProcessKind.Platform;
        }
        if (windowTitle is not null && Regex.IsMatch(windowTitle, Regex.Escape(rules.GameWindowTitlePattern), RegexOptions.IgnoreCase))
        {
            return TrackedProcessKind.GameWindow;
        }
        return TrackedProcessKind.Other;
    }

    private static bool AnyProcessMatches(IReadOnlyList<string> patterns) => FirstProcessMatching(patterns) is not null;

    private static int? FirstProcessMatching(IReadOnlyList<string> patterns)
    {
        foreach (var (id, name) in ProcessSnapshot.GetRunning())
        {
            if (GameDetectionEngine.MatchesAny(name, patterns))
            {
                return id;
            }
        }
        return null;
    }

    private static bool GameWindowPresent(DetectionRules rules)
        => WindowNative.GetVisibleWindows()
            .Any(w => w.Title.Contains(rules.GameWindowTitlePattern, StringComparison.OrdinalIgnoreCase));
}
