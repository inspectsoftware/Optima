using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Launch;

public enum LaunchPhase
{
    Idle,
    Validating,
    ApplyingPerformanceProfile,
    ConfiguringDisplay,
    StartingPlatform,
    WaitingForGame,
    Monitoring,
    Restoring,
    Completed,
    Failed,
}

public sealed record LaunchProgress(LaunchPhase Phase, string Message);

/// <summary>
/// Something a session went without: a step that failed and was not worth stopping the launch for.
/// </summary>
public sealed record LaunchWarning(string Code, string Title, string Detail);

public sealed record LaunchResult
{
    public required bool Success { get; init; }
    public UserFriendlyError? Error { get; init; }
    public SessionRecord? Session { get; init; }

    /// <summary>What the session went without. A session can succeed and still carry these.</summary>
    public IReadOnlyList<LaunchWarning> Warnings { get; init; } = [];
}

/// <summary>
/// The §5 pipeline: validate → snapshot → apply profile → configure display → launch → detect runtime → monitor → wait
/// for exit → restore → session stats.
/// </summary>
public sealed class LaunchOrchestrator
{
    private readonly IGameDetector _detector;
    private readonly IReadOnlyList<IGameLauncher> _launchers;
    private readonly IVirtualDisplayProvider _virtualDisplay;
    private readonly IDisplayService _displayService;
    private readonly IPowerProfileService _power;
    private readonly IProcessMonitor _processMonitor;
    private readonly IProcessOptimizer _processOptimizer;
    private readonly IBackgroundCleanupService _cleanup;
    private readonly IRecoveryService _recovery;
    private readonly IPerformanceMetricsProvider _metrics;
    private readonly INetworkQualityMonitor _network;
    private readonly ISessionStore _sessionStore;
    private readonly ITweakService _tweaks;
    private readonly SettingsService? _settings;
    private readonly ILogger<LaunchOrchestrator> _logger;

    private int _running;

    /// <summary>
    /// The phase last reported. One session runs at a time, so a single field is enough for the
    /// catch-all to say where a failure it knows nothing else about happened.
    /// </summary>
    private LaunchPhase _phase;

    /// <summary>How often the session re-asserts the applied profile on the game process by default.</summary>
    public static readonly TimeSpan DefaultPriorityKeeperInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The keeper's wake-up interval. Anything on Windows can change a process's scheduling class
    /// or power-throttling state while a game runs (another tool, an update helper, the shell), so
    /// the session corrects the game process for as long as it lives instead of trusting the one
    /// application at the start. The property exists so a test can shorten the wait.
    /// </summary>
    public TimeSpan PriorityKeeperInterval { get; set; } = DefaultPriorityKeeperInterval;

    /// <summary>Mutable per-session state, so the catch paths always restore the latest snapshot.</summary>
    private sealed class SessionContext
    {
        public required SystemStateSnapshot Snapshot { get; set; }
        public bool MetricsStarted { get; set; }
        public List<LaunchWarning> Warnings { get; } = [];
    }

    public LaunchOrchestrator(
        IGameDetector detector,
        IEnumerable<IGameLauncher> launchers,
        IVirtualDisplayProvider virtualDisplay,
        IDisplayService displayService,
        IPowerProfileService power,
        IProcessMonitor processMonitor,
        IProcessOptimizer processOptimizer,
        IBackgroundCleanupService cleanup,
        IRecoveryService recovery,
        IPerformanceMetricsProvider metrics,
        INetworkQualityMonitor network,
        ISessionStore sessionStore,
        ITweakService tweaks,
        ILogger<LaunchOrchestrator> logger,
        SettingsService? settings = null)
    {
        _detector = detector;
        _launchers = launchers.OrderBy(l => l.Order).ToList();
        _virtualDisplay = virtualDisplay;
        _displayService = displayService;
        _power = power;
        _processMonitor = processMonitor;
        _processOptimizer = processOptimizer;
        _cleanup = cleanup;
        _recovery = recovery;
        _metrics = metrics;
        _network = network;
        _sessionStore = sessionStore;
        _tweaks = tweaks;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<LaunchProgress>? ProgressChanged;

    /// <summary>
    /// Raised once a session has finished, whatever its outcome. It exists for watch mode: a session
    /// Optima ran can end while the game is still up (the run was stopped or cancelled and the player
    /// kept playing), and watch mode has to notice that without polling for it.
    /// </summary>
    public event Action? SessionEnded;

    public bool IsSessionActive => Volatile.Read(ref _running) == 1;

    public Task<LaunchResult> RunSessionAsync(LaunchProfile profile, CancellationToken ct = default)
        => RunSessionAsync(profile, LaunchKind.Play, ct);

    public Task<LaunchResult> RunSessionAsync(LaunchProfile profile, LaunchKind kind, CancellationToken ct = default)
        => RunGatedAsync(profile, async (context, token) =>
        {
            Report(LaunchPhase.Validating, "Checking Google Play Games and Critical Ops…");
            var platform = await _detector.DetectPlatformAsync(token).ConfigureAwait(false);
            if (platform is null)
            {
                return Fail("GPG_NOT_FOUND", "Google Play Games was not found.",
                    "Install Google Play Games for PC from Google, then run diagnostics.",
                    "Install Google Play Games (beta) from Google's site",
                    "If it is installed in a custom location, set the path in Settings");
            }

            var game = await _detector.DetectTargetGameAsync(token).ConfigureAwait(false);
            if (game is null)
            {
                return Fail("GAME_NOT_FOUND", "Critical Ops is not installed in Google Play Games.",
                    "Open Google Play Games and install Critical Ops, then try again.",
                    "Open Google Play Games and install Critical Ops",
                    "Run detection again from the Diagnostics page");
            }

            await _recovery.SavePendingAsync(context.Snapshot, token).ConfigureAwait(false);
            await ApplyEnvironmentAsync(profile, context, token).ConfigureAwait(false);

            Report(LaunchPhase.StartingPlatform, "Launching Critical Ops through Google Play Games…");
            var launched = false;
            foreach (var launcher in _launchers)
            {
                token.ThrowIfCancellationRequested();
                if (!await launcher.CanLaunchAsync(game, token).ConfigureAwait(false))
                {
                    continue;
                }
                _logger.LogInformation("Trying launch strategy {Strategy}", launcher.Name);
                if (await launcher.LaunchAsync(game, token).ConfigureAwait(false))
                {
                    launched = true;
                    _logger.LogInformation("Launch strategy {Strategy} succeeded", launcher.Name);
                    break;
                }
            }

            if (!launched)
            {
                return await FailAndRestoreAsync(context, "LAUNCH_FAILED",
                    "Could not start Critical Ops.",
                    "Every launch strategy failed. Google Play Games may need an update or a repair.",
                    "Start Google Play Games manually and check it opens",
                    "Re-run detection from the Diagnostics page",
                    "Configure a custom launch command in Settings").ConfigureAwait(false);
            }

            Report(LaunchPhase.WaitingForGame, "Waiting for the game to start…");
            var emulatorPid = await _processMonitor.WaitForGameStartAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
            if (emulatorPid is null)
            {
                return await FailAndRestoreAsync(context, "GAME_START_TIMEOUT",
                    "The game did not start within three minutes.",
                    "Google Play Games opened but the game runtime never appeared.",
                    "Check Google Play Games for sign-in prompts or updates",
                    "Try launching once from Google Play Games directly").ConfigureAwait(false);
            }
            _logger.LogInformation("Game process detected (emulator PID {Pid})", emulatorPid);

            return await MonitorAndCompleteAsync(
                profile, game.PackageId, emulatorPid.Value, context, kind, captureAllowed: true, token).ConfigureAwait(false);
        }, ct);

    public Task<LaunchResult> AttachToRunningGameAsync(
        LaunchProfile profile, int emulatorPid, bool captureAllowed, CancellationToken ct = default)
        => RunGatedAsync(profile, async (context, token) =>
        {
            Report(LaunchPhase.Validating, "Game detected. Applying the selected profile…");
            var game = await _detector.DetectTargetGameAsync(token).ConfigureAwait(false);

            await _recovery.SavePendingAsync(context.Snapshot, token).ConfigureAwait(false);
            await ApplyEnvironmentAsync(profile, context, token).ConfigureAwait(false);

            return await MonitorAndCompleteAsync(
                profile, game?.PackageId ?? "unknown", emulatorPid, context, LaunchKind.Watch, captureAllowed, token).ConfigureAwait(false);
        }, ct);

    private async Task<LaunchResult> RunGatedAsync(
        LaunchProfile profile,
        Func<SessionContext, CancellationToken, Task<LaunchResult>> body,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Fail("SESSION_ACTIVE", "A session is already running.",
                "Wait for the current game session to finish before starting another.");
        }

        _phase = LaunchPhase.Idle;
        var context = new SessionContext { Snapshot = new SystemStateSnapshot { ProfileName = profile.Name } };
        try
        {
            return WithWarnings(context, await body(context, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Session cancelled, restoring system state");
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Completed, "Cancelled. Settings restored.");
            return WithWarnings(context,
                Fail("CANCELLED", "The session was cancelled.", "All temporary settings were restored."));
        }
        catch (OptimaException ex)
        {
            _logger.LogError(ex, "Session failed: {Code}", ex.Error.Code);
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Failed, ex.Error.Title);
            return WithWarnings(context, new LaunchResult { Success = false, Error = ex.Error });
        }
        catch (Exception ex)
        {
            // Read before the Failed report below overwrites it.
            var phase = _phase;
            _logger.LogError(ex, "Unexpected session failure during {Phase}", phase);
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Failed, "Unexpected error");
            return WithWarnings(context, new LaunchResult
            {
                Success = false,
                Error = new UserFriendlyError
                {
                    Code = "UNEXPECTED",
                    Title = "Something went wrong during the session.",
                    Explanation = $"The session stopped while {Describe(phase)}. All temporary settings were restored.",
                    SuggestedFixes =
                    [
                        "Open developer details below: it names the step and what Windows reported",
                        "Check the Logs page for details",
                        "Run Diagnostics to verify the environment",
                    ],
                    DeveloperDetails = $"phase: {phase}{Environment.NewLine}{ExceptionDetail.Capture(ex).FullText}",
                },
            });
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            // Raised after the gate closes, so a listener asking IsSessionActive sees the truth, and
            // guarded, because a listener must never replace the session result with its own failure.
            try
            {
                SessionEnded?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A session-end listener failed");
            }
        }
    }

    private async Task ApplyEnvironmentAsync(LaunchProfile profile, SessionContext context, CancellationToken ct)
    {
        Report(LaunchPhase.ApplyingPerformanceProfile, "Applying performance profile…");
        if (profile.Performance.PowerPlan != PowerPlanKind.Unchanged)
        {
            await RunOptionalAsync(context, "LAUNCH_STEP_SKIPPED", "The power plan was left as it was", async () =>
            {
                var previous = await _power.ApplyAsync(profile.Performance.PowerPlan, ct).ConfigureAwait(false);
                context.Snapshot = context.Snapshot with { PreviousPowerScheme = previous };
                await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
                _logger.LogInformation("Performance profile applied: power plan {Plan}", profile.Performance.PowerPlan);
            }).ConfigureAwait(false);
        }

        if (profile.Performance.CleanupProcessNames.Count > 0)
        {
            await RunOptionalAsync(context, "LAUNCH_STEP_SKIPPED", "Background cleanup was skipped", async () =>
            {
                var closed = await _cleanup.CloseAsync(profile.Performance.CleanupProcessNames, ct).ConfigureAwait(false);
                if (closed.Count > 0)
                {
                    _logger.LogInformation("Background cleanup closed: {Processes}", string.Join(", ", closed));
                }
            }).ConfigureAwait(false);
        }

        if (profile.Display.VirtualDisplay)
        {
            Report(LaunchPhase.ConfiguringDisplay, $"Configuring virtual display {profile.Display.Mode}…");

            context.Snapshot = context.Snapshot with { VirtualDisplayConfigured = true };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

            await _virtualDisplay.InitializeAsync(ct).ConfigureAwait(false);

            var wasActive = await _virtualDisplay.IsDisplayActiveAsync(ct).ConfigureAwait(false);
            if (!wasActive)
            {
                await _virtualDisplay.EnableDisplayAsync(ct).ConfigureAwait(false);
                context.Snapshot = context.Snapshot with { VirtualDisplayEnabledByUs = true };
                await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
            }

            await _virtualDisplay.SetModeAsync(profile.Display.Mode, ct).ConfigureAwait(false);
            _logger.LogInformation("Resolution applied: {Mode} on virtual display", profile.Display.Mode);

            var topology = await _displayService.CaptureTopologyAsync(ct).ConfigureAwait(false);
            context.Snapshot = context.Snapshot with { DisplayTopology = topology };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

            if (profile.Display.MakePrimary
                && await _virtualDisplay.GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } displayInfo)
            {
                await _displayService.MakePrimaryAsync(displayInfo.DeviceName, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The performance profile as it will actually be applied. The Settings page can pin one
    /// priority for the game process across every profile, so a player does not have to edit a
    /// profile to get it; Unchanged there leaves the profile's own choice in charge.
    /// </summary>
    private PerformanceProfile WithSettingsGamePriority(PerformanceProfile profile)
    {
        var configured = _settings?.Current?.GamePriority;
        return Enum.TryParse<ProcessPriorityLevel>(configured, ignoreCase: true, out var level)
            && level != ProcessPriorityLevel.Unchanged
            ? profile with { Priority = level }
            : profile;
    }

    private async Task<LaunchResult> MonitorAndCompleteAsync(
        LaunchProfile profile, string packageId, int emulatorPid,
        SessionContext context, LaunchKind kind, bool captureAllowed, CancellationToken ct)
    {
        var performance = WithSettingsGamePriority(profile.Performance);
        // The game is already running here; a tuning failure must not end a session it is part of.
        ProcessStateSnapshot? procSnapshot = null;
        await RunOptionalAsync(context, "LAUNCH_STEP_SKIPPED", "The game's process was not tuned", async () =>
        {
            procSnapshot = await _processOptimizer.ApplyAsync(emulatorPid, performance, ct).ConfigureAwait(false);
            if (procSnapshot is not null)
            {
                context.Snapshot = context.Snapshot with { ProcessStates = [.. context.Snapshot.ProcessStates, procSnapshot] };
                await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        Report(LaunchPhase.Monitoring, "Critical Ops is running.");
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var candidatePids = await CollectCandidatePidsAsync(emulatorPid, ct).ConfigureAwait(false);

        if (captureAllowed && await _metrics.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _metrics.StartAsync(candidatePids, ct).ConfigureAwait(false);
                context.MetricsStarted = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Frametime capture unavailable, continuing without FPS metrics");
            }
        }

        try
        {
            await _network.StartAsync(candidatePids, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Network quality monitoring unavailable for this session");
        }

        await MonitorGameAsync(performance, procSnapshot, ct).ConfigureAwait(false);
        stopwatch.Stop();
        _logger.LogInformation("Game exited after {Duration}", stopwatch.Elapsed);

        Report(LaunchPhase.Restoring, "Restoring system settings…");
        // A session whose capture never started must save empty stats, not whatever the
        // provider still holds from a previous session.
        var captured = context.MetricsStarted;
        if (context.MetricsStarted)
        {
            await _metrics.StopAsync().ConfigureAwait(false);
            context.MetricsStarted = false;
        }
        var networkStats = await StopNetworkAsync().ConfigureAwait(false);
        await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);

        var session = new SessionRecord
        {
            ProfileName = profile.Name,
            GamePackageId = packageId,
            StartedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Stats = captured ? _metrics.GetSessionStats() : SessionStats.Empty,
            FpsSamples = captured ? _metrics.GetFpsSamples() : [],
            TweakIds = await GetEnabledTweakIdsAsync().ConfigureAwait(false),
            ProfileHash = LaunchProfileHasher.ComputeHash(profile),
            LaunchKind = kind,
            Network = networkStats,
        };
        // Everything is restored by now. A history that cannot be written must not turn a finished
        // session into a failed one, nor send the catch-all through a second restore.
        await RunOptionalAsync(context, "SESSION_NOT_SAVED", "This session was not saved to the history", async () =>
        {
            var sessionId = await _sessionStore.SaveSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            session = session with { Id = sessionId };
        }).ConfigureAwait(false);

        Report(LaunchPhase.Completed, "Session complete. Settings restored.");
        return new LaunchResult { Success = true, Session = session };
    }

    /// <summary>
    /// Waits for the game to exit while the priority keeper holds the applied profile on the
    /// game process. The monitor call is the primary wait; the keeper only wakes on the same
    /// interval and is cancelled with the session, so an exit is never delayed by it.
    /// </summary>
    private async Task MonitorGameAsync(PerformanceProfile performance, ProcessStateSnapshot? baseline, CancellationToken ct)
    {
        var exit = _processMonitor.WaitForGameExitAsync(ct);
        if (baseline is null)
        {
            await exit.ConfigureAwait(false);
            return;
        }

        using var keeperCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keeper = KeepPriorityAsync(baseline, performance, keeperCts.Token);
        try
        {
            await exit.ConfigureAwait(false);
        }
        finally
        {
            await keeperCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await keeper.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task KeepPriorityAsync(ProcessStateSnapshot baseline, PerformanceProfile performance, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(PriorityKeeperInterval, ct).ConfigureAwait(false);
            try
            {
                await _processOptimizer.ReassertAsync(baseline, performance, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Priority keeper pass failed for pid {Pid}", baseline.ProcessId);
            }
        }
    }

    private async Task StopMonitoringAsync(SessionContext context)
    {
        if (context.MetricsStarted)
        {
            try
            {
                await _metrics.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frametime capture did not stop cleanly");
            }
            context.MetricsStarted = false;
        }
        await StopNetworkAsync().ConfigureAwait(false);
    }

    private async Task<NetworkQualityStats?> StopNetworkAsync()
    {
        try
        {
            return await _network.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Network quality monitor did not stop cleanly");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> GetEnabledTweakIdsAsync()
    {
        try
        {
            var states = await _tweaks.GetStatesAsync(CancellationToken.None).ConfigureAwait(false);
            return states
                .Where(s => s.Status == TweakStatus.Enabled)
                .Select(s => s.Definition.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read tweak states for the session record");
            return [];
        }
    }

    private async Task<IReadOnlyList<int>> CollectCandidatePidsAsync(int emulatorPid, CancellationToken ct)
    {
        var pids = new List<int> { emulatorPid };
        try
        {
            var tracked = await _processMonitor.GetTrackedProcessesAsync(ct).ConfigureAwait(false);
            pids.AddRange(tracked
                .Where(p => p.Kind is not TrackedProcessKind.Other)
                .Select(p => p.ProcessId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not enumerate tracked processes, capturing the emulator pid only");
        }
        return pids.Distinct().Take(16).ToList();
    }

    /// <summary>
    /// Runs a step the game does not need in order to start or to have run. A failure is written
    /// down as a warning on the session and the session carries on: a power plan this PC does not
    /// offer is no reason to refuse to start the game.
    /// </summary>
    private async Task RunOptionalAsync(SessionContext context, string code, string title, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OptimaException ex)
        {
            // The step already knows what went wrong and says it better than the generic title.
            _logger.LogWarning(ex, "Session step skipped ({Code}): {Title}", ex.Error.Code, ex.Error.Title);
            context.Warnings.Add(new LaunchWarning(ex.Error.Code, ex.Error.Title, ex.Error.Explanation));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session step skipped ({Code}): {Title}", code, title);
            context.Warnings.Add(new LaunchWarning(code, title, ExceptionDetail.Capture(ex).Summary));
        }
    }

    private static LaunchResult WithWarnings(SessionContext context, LaunchResult result)
        => context.Warnings.Count == 0 ? result : result with { Warnings = [.. context.Warnings] };

    private static string Describe(LaunchPhase phase) => phase switch
    {
        LaunchPhase.Validating => "checking Google Play Games and Critical Ops",
        LaunchPhase.ApplyingPerformanceProfile => "applying the performance profile",
        LaunchPhase.ConfiguringDisplay => "configuring the virtual display",
        LaunchPhase.StartingPlatform => "starting Google Play Games",
        LaunchPhase.WaitingForGame => "waiting for the game to start",
        LaunchPhase.Monitoring => "the game was running",
        LaunchPhase.Restoring => "restoring system settings",
        _ => "getting ready",
    };

    private async Task<LaunchResult> FailAndRestoreAsync(
        SessionContext context, string code, string title, string explanation, params string[] fixes)
    {
        await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
        Report(LaunchPhase.Failed, title);
        return Fail(code, title, explanation, fixes);
    }

    private LaunchResult Fail(string code, string title, string explanation, params string[] fixes)
        => new()
        {
            Success = false,
            Error = new UserFriendlyError
            {
                Code = code,
                Title = title,
                Explanation = explanation,
                SuggestedFixes = fixes,
            },
        };

    private void Report(LaunchPhase phase, string message)
    {
        _phase = phase;
        _logger.LogInformation("[{Phase}] {Message}", phase, message);
        ProgressChanged?.Invoke(this, new LaunchProgress(phase, message));
    }
}
