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
    private readonly ILaunchSupport? _support;
    private readonly ILogger<LaunchOrchestrator> _logger;

    private int _running;

    /// <summary>
    /// Cancelled once, by <see cref="StopAsync"/>, when Optima is closing. Every session's token is
    /// linked to it and it is never reset: nothing may start a session in a process that is leaving.
    /// </summary>
    private readonly CancellationTokenSource _stop = new();

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
        SettingsService? settings = null,
        ILaunchSupport? support = null)
    {
        _support = support;
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

    /// <summary>
    /// Ends the running session because Optima is closing, and refuses every later one. A session
    /// whose game is up ends the way it does when the game exits: capture stops, the system is put
    /// back and the session is saved. One that has not got that far is cancelled and restored.
    /// </summary>
    /// <returns>False when the session was still running after <paramref name="timeout"/>.</returns>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        // Not awaited, and on the pool: the callbacks run a good part of the session's ending
        // inline, and neither the caller's thread nor the timeout may be held by that.
        _ = _stop.CancelAsync();
        var waited = Stopwatch.StartNew();
        while (IsSessionActive && waited.Elapsed < timeout)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
        return !IsSessionActive;
    }

    public Task<LaunchResult> RunSessionAsync(LaunchProfile profile, CancellationToken ct = default)
        => RunSessionAsync(profile, LaunchKind.Play, ct);

    public Task<LaunchResult> RunSessionAsync(LaunchProfile requested, LaunchKind kind, CancellationToken ct = default)
    {
        var profile = WithSettingsDisplay(requested);
        return RunGatedAsync(profile, async (context, token) =>
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
                    "Run detection again from the Checks tab on the Debug page");
            }

            await PreflightAsync(token).ConfigureAwait(false);
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
                    "Re-run detection from the Checks tab on the Debug page",
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
    }

    public Task<LaunchResult> AttachToRunningGameAsync(
        LaunchProfile requested, int emulatorPid, bool captureAllowed, CancellationToken ct = default)
    {
        var profile = WithSettingsDisplay(requested);
        return RunGatedAsync(profile, async (context, token) =>
        {
            Report(LaunchPhase.Validating, "Game detected. Applying the selected profile…");
            var game = await _detector.DetectTargetGameAsync(token).ConfigureAwait(false);

            await _recovery.SavePendingAsync(context.Snapshot, token).ConfigureAwait(false);
            await ApplyEnvironmentAsync(profile, context, token).ConfigureAwait(false);

            return await MonitorAndCompleteAsync(
                profile, game?.PackageId ?? "unknown", emulatorPid, context, LaunchKind.Watch, captureAllowed, token).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// The profile with the display the session will really use. Whether the game runs on the
    /// virtual display, and at which mode, is one choice on the Display page for every profile;
    /// what a profile itself still says about the display only counts until that choice exists
    /// (a first start that has not got to it yet, or a host without settings).
    /// </summary>
    private LaunchProfile WithSettingsDisplay(LaunchProfile profile)
        => _settings?.Current is { VirtualDisplayEnabled: not null } settings
            ? profile with { Display = settings.EffectiveDisplay }
            : profile;

    private async Task<LaunchResult> RunGatedAsync(
        LaunchProfile profile,
        Func<SessionContext, CancellationToken, Task<LaunchResult>> body,
        CancellationToken ct)
    {
        // Before the gate: the session that Optima's exit has just ended leaves the game running,
        // and watch mode must not answer that by attaching to it again.
        if (_stop.IsCancellationRequested)
        {
            return Fail("CANCELLED", "Optima is closing.", "No session was started.");
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Fail("SESSION_ACTIVE", "A session is already running.",
                "Wait for the current game session to finish before starting another.");
        }

        _phase = LaunchPhase.Idle;
        var context = new SessionContext { Snapshot = new SystemStateSnapshot { ProfileName = profile.Name } };
        try
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            return WithWarnings(context, await body(context, session.Token).ConfigureAwait(false));
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
                        "Check the log on the Debug page for details",
                        "Run the checks on the Debug page to verify the environment",
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
            try
            {
                await ConfigureVirtualDisplayAsync(profile.Display, context, ct).ConfigureAwait(false);
            }
            catch (OptimaException ex) when (ex.Error.Code == "VDD_NOT_INSTALLED")
            {
                // The choice says "virtual display" and the driver is not on this PC (never
                // installed, or uninstalled since). That is no reason to refuse to start the game:
                // it runs on the real screen, and the session says what it went without.
                _logger.LogWarning("The virtual display is switched on but its driver is not installed; the game runs on the real screen");
                context.Warnings.Add(new LaunchWarning(ex.Error.Code,
                    "The virtual display driver is not installed",
                    "The game ran on your real screen. Install the driver on the Display page, or switch the virtual display off there."));
                context.Snapshot = context.Snapshot with { VirtualDisplayConfigured = false };
                await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ConfigureVirtualDisplayAsync(DisplayProfile display, SessionContext context, CancellationToken ct)
    {
        Report(LaunchPhase.ConfiguringDisplay, $"Configuring virtual display {display.Mode}…");

        context.Snapshot = context.Snapshot with { VirtualDisplayConfigured = true };
        await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

        await _virtualDisplay.InitializeAsync(ct).ConfigureAwait(false);

        var wasActive = await _virtualDisplay.IsDisplayActiveAsync(ct).ConfigureAwait(false);
        if (!wasActive)
        {
            await RunEssentialAsync(() => _virtualDisplay.EnableDisplayAsync(ct), ct).ConfigureAwait(false);
            context.Snapshot = context.Snapshot with { VirtualDisplayEnabledByUs = true };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
        }

        await _virtualDisplay.SetModeAsync(display.Mode, ct).ConfigureAwait(false);
        _logger.LogInformation("Resolution applied: {Mode} on virtual display", display.Mode);

        var topology = await _displayService.CaptureTopologyAsync(ct).ConfigureAwait(false);
        context.Snapshot = context.Snapshot with { DisplayTopology = topology };
        await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

        if (display.MakePrimary
            && await _virtualDisplay.GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } displayInfo)
        {
            // Not worth a launch: the display is up at its mode either way, and the game can be
            // moved onto it by hand. The session says that it was not made the main screen.
            await RunOptionalAsync(context, "DISPLAY_PRIMARY_FAILED", "The virtual display was not made the main screen",
                () => _displayService.MakePrimaryAsync(displayInfo.DeviceName, ct)).ConfigureAwait(false);
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

        try
        {
            await MonitorGameAsync(performance, procSnapshot, ct).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation("Game exited after {Duration}", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Optima is closing with the game still up. The session ends here the way it does when
            // the game exits, so what it measured is saved and the system is put back. A cancel
            // restores too, but throws the record away.
            stopwatch.Stop();
            _logger.LogInformation("Optima is closing after {Duration}; ending the session with the game still running", stopwatch.Elapsed);
        }

        Report(LaunchPhase.Restoring, "Restoring system settings…");
        // A session whose capture never started must save empty stats, not whatever the
        // provider still holds from a previous session.
        var captured = context.MetricsStarted;
        var networkStats = await StopMonitoringAsync(context).ConfigureAwait(false);
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

    private async Task<NetworkQualityStats?> StopMonitoringAsync(SessionContext context)
    {
        if (context.MetricsStarted)
        {
            try
            {
                await _metrics.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Every failure, a cancellation too: a helper that does not answer the stop in time
                // surfaces as one, and let through it would turn a finished session into a cancelled
                // one and drop its record. The provider then reports what it sampled live.
                _logger.LogWarning(ex, "Frametime capture did not stop cleanly");
            }
            context.MetricsStarted = false;
        }
        return await StopNetworkAsync().ConfigureAwait(false);
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
    /// The quick checks, before anything is changed. Whatever they find goes on the issue list; a
    /// launch is never stopped by them and never made to wait for them.
    /// </summary>
    private async Task PreflightAsync(CancellationToken ct)
    {
        if (_support is null)
        {
            return;
        }
        try
        {
            await _support.PreflightAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "The checks before the launch failed; launching without them");
        }
    }

    /// <summary>
    /// Runs a step the session cannot do without. When it fails with a fault a repair is known
    /// for, the repair runs and the step gets one more try; a second failure is the session's
    /// failure, under the error the step itself gave.
    /// </summary>
    private async Task RunEssentialAsync(Func<Task> step, CancellationToken ct)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OptimaException ex) when (_support is { } support)
        {
            if (!await support.TryRepairAsync(ex.Error.Code, ct).ConfigureAwait(false))
            {
                throw;
            }
            _logger.LogInformation("A repair ran for {Code}; trying the step once more", ex.Error.Code);
            await step().ConfigureAwait(false);
        }
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
