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
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    /// <summary>
    /// The last sweep of the process list and the window list, shared by every caller.
    ///
    /// Two loops want this state (the always-on presence loop and, during a run, the game exit
    /// loop), and before this each of them walked the whole Toolhelp list and asked Windows for
    /// the title of every visible window, every time. One sweep per second answers all of them.
    /// </summary>
    private PresenceTick? _tick;

    public WindowsProcessMonitor(Func<CancellationToken, Task<DetectionRules>> rulesProvider, ILogger<WindowsProcessMonitor> logger)
    {
        _rulesProvider = rulesProvider;
        _logger = logger;
    }

    private sealed record PresenceTick(
        int? EmulatorProcessId,
        bool GameWindowPresent,
        GameRuntimeState State,
        DateTimeOffset At);

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
        return (await GetTickAsync(rules, ct).ConfigureAwait(false)).State;
    }

    public async Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var tick = await GetTickAsync(rules, ct).ConfigureAwait(false);
            if (tick.EmulatorProcessId is { } emulatorPid && tick.GameWindowPresent)
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

            var tick = await GetTickAsync(rules, ct).ConfigureAwait(false);
            if (tick.EmulatorProcessId is null)
            {
                _logger.LogInformation("Game exited (emulator process ended)");
                return;
            }

            absentPolls = tick.GameWindowPresent ? 0 : absentPolls + 1;
            if (absentPolls >= ExitConfirmationPolls)
            {
                _logger.LogInformation("Game exited (game window closed)");
                return;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The current sweep, refreshed at most once per poll interval however many callers ask for it.</summary>
    private async Task<PresenceTick> GetTickAsync(DetectionRules rules, CancellationToken ct)
    {
        var current = _tick;
        if (current is not null && DateTimeOffset.UtcNow - current.At < PollInterval)
        {
            return current;
        }

        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _tick;
            if (current is not null && DateTimeOffset.UtcNow - current.At < PollInterval)
            {
                return current;
            }

            var tick = await Task.Run(() => Capture(rules), ct).ConfigureAwait(false);
            _tick = tick;
            return tick;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private static PresenceTick Capture(DetectionRules rules)
    {
        var processes = ProcessSnapshot.GetRunning();
        var windows = WindowNative.GetVisibleWindows();

        var emulatorPid = FirstProcessMatching(processes, rules.EmulatorProcessPatterns);
        var windowPresent = windows.Any(w => w.Title.Contains(rules.GameWindowTitlePattern, StringComparison.OrdinalIgnoreCase));
        var state = emulatorPid is not null
            ? windowPresent ? GameRuntimeState.Running : GameRuntimeState.Starting
            : GameRuntimeState.NotRunning;
        return new PresenceTick(emulatorPid, windowPresent, state, DateTimeOffset.UtcNow);
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
        // The title is matched literally, which is what the escaped-regex test this replaced did,
        // without compiling a pattern for every process on every sweep.
        if (windowTitle is not null && windowTitle.Contains(rules.GameWindowTitlePattern, StringComparison.OrdinalIgnoreCase))
        {
            return TrackedProcessKind.GameWindow;
        }
        return TrackedProcessKind.Other;
    }

    private static int? FirstProcessMatching(IReadOnlyList<(int Id, string Name)> processes, IReadOnlyList<string> patterns)
    {
        foreach (var (id, name) in processes)
        {
            if (GameDetectionEngine.MatchesAny(name, patterns))
            {
                return id;
            }
        }
        return null;
    }
}
