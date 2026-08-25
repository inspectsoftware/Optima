using System.Diagnostics;
using Optima.Core.Abstractions;
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

public sealed record LaunchResult
{
    public required bool Success { get; init; }
    public UserFriendlyError? Error { get; init; }
    public SessionRecord? Session { get; init; }
}

/// <summary>
/// The §5 pipeline: validate → snapshot → apply profile → configure display → launch →
/// detect runtime → monitor → wait for exit → restore → session stats. The recovery snapshot
/// is persisted *before* each mutating step so a crash at any point is recoverable (§18).
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
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<LaunchOrchestrator> _logger;

    private int _running; // 0/1 gate so PLAY cannot double-fire

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
        ISessionStore sessionStore,
        ILogger<LaunchOrchestrator> logger)
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
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public event EventHandler<LaunchProgress>? ProgressChanged;

    public bool IsSessionActive => Volatile.Read(ref _running) == 1;

    public async Task<LaunchResult> RunSessionAsync(LaunchProfile profile, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Fail("SESSION_ACTIVE", "A session is already running.",
                "Wait for the current game session to finish before starting another.");
        }

        var snapshot = new SystemStateSnapshot { ProfileName = profile.Name };
        var stopwatch = new Stopwatch();
        var metricsStarted = false;

        try
        {
            // ---- Validate -------------------------------------------------------------
            Report(LaunchPhase.Validating, "Checking Google Play Games and Critical Ops…");
            var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
            if (platform is null)
            {
                return Fail("GPG_NOT_FOUND", "Google Play Games was not found.",
                    "Install Google Play Games for PC from Google, then run diagnostics.",
                    "Install Google Play Games (beta) from Google's site",
                    "If it is installed in a custom location, set the path in Settings");
            }

            var game = await _detector.DetectTargetGameAsync(ct).ConfigureAwait(false);
            if (game is null)
            {
                return Fail("GAME_NOT_FOUND", "Critical Ops is not installed in Google Play Games.",
                    "Open Google Play Games and install Critical Ops, then try again.",
                    "Open Google Play Games and install Critical Ops",
                    "Run detection again from the Diagnostics page");
            }

            // Snapshot exists on disk from here on, so any crash is recoverable.
            await _recovery.SavePendingAsync(snapshot, ct).ConfigureAwait(false);

            // ---- Performance profile --------------------------------------------------
            Report(LaunchPhase.ApplyingPerformanceProfile, "Applying performance profile…");
            if (profile.Performance.PowerPlan != PowerPlanKind.Unchanged)
            {
                var previous = await _power.ApplyAsync(profile.Performance.PowerPlan, ct).ConfigureAwait(false);
                snapshot = snapshot with { PreviousPowerScheme = previous };
                await _recovery.UpdatePendingAsync(snapshot, ct).ConfigureAwait(false);
                _logger.LogInformation("Performance profile applied: power plan {Plan}", profile.Performance.PowerPlan);
            }

            if (profile.Performance.CleanupProcessNames.Count > 0)
            {
                var closed = await _cleanup.CloseAsync(profile.Performance.CleanupProcessNames, ct).ConfigureAwait(false);
                if (closed.Count > 0)
                {
                    _logger.LogInformation("Background cleanup closed: {Processes}", string.Join(", ", closed));
                }
            }

            // ---- Virtual display ------------------------------------------------------
            if (profile.Display.VirtualDisplay)
            {
                Report(LaunchPhase.ConfiguringDisplay, $"Configuring virtual display {profile.Display.Mode}…");

                snapshot = snapshot with { VirtualDisplayConfigured = true };
                await _recovery.UpdatePendingAsync(snapshot, ct).ConfigureAwait(false);

                // Initialize captures the provider's restore baseline (settings backup, device state).
                await _virtualDisplay.InitializeAsync(ct).ConfigureAwait(false);

                var wasActive = await _virtualDisplay.IsDisplayActiveAsync(ct).ConfigureAwait(false);
                if (!wasActive)
                {
                    await _virtualDisplay.EnableDisplayAsync(ct).ConfigureAwait(false);
                    snapshot = snapshot with { VirtualDisplayEnabledByUs = true };
                    await _recovery.UpdatePendingAsync(snapshot, ct).ConfigureAwait(false);
                }

                await _virtualDisplay.SetModeAsync(profile.Display.Mode, ct).ConfigureAwait(false);
                _logger.LogInformation("Resolution applied: {Mode} on virtual display", profile.Display.Mode);

                // Capture the topology AFTER driver configuration: a driver reload re-creates the
                // virtual display with new CCD target ids, so an earlier snapshot would reference
                // targets that no longer exist and could not be re-applied.
                var topology = await _displayService.CaptureTopologyAsync(ct).ConfigureAwait(false);
                snapshot = snapshot with { DisplayTopology = topology };
                await _recovery.UpdatePendingAsync(snapshot, ct).ConfigureAwait(false);

                if (profile.Display.MakePrimary
                    && await _virtualDisplay.GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } displayInfo)
                {
                    // Opt-in only: on a local machine the virtual display is invisible, so making
                    // it primary is for capture/streaming setups. Topology restore reverts it.
                    await _displayService.MakePrimaryAsync(displayInfo.DeviceName, ct).ConfigureAwait(false);
                }
            }

            // ---- Launch ---------------------------------------------------------------
            Report(LaunchPhase.StartingPlatform, "Launching Critical Ops through Google Play Games…");
            var launched = false;
            foreach (var launcher in _launchers)
            {
                ct.ThrowIfCancellationRequested();
                if (!await launcher.CanLaunchAsync(game, ct).ConfigureAwait(false))
                {
                    continue;
                }
                _logger.LogInformation("Trying launch strategy {Strategy}", launcher.Name);
                if (await launcher.LaunchAsync(game, ct).ConfigureAwait(false))
                {
                    launched = true;
                    _logger.LogInformation("Launch strategy {Strategy} succeeded", launcher.Name);
                    break;
                }
            }

            if (!launched)
            {
                return await FailAndRestoreAsync(snapshot, "LAUNCH_FAILED",
                    "Could not start Critical Ops.",
                    "Every launch strategy failed. Google Play Games may need an update or a repair.",
                    ct,
                    "Start Google Play Games manually and check it opens",
                    "Re-run detection from the Diagnostics page",
                    "Configure a custom launch command in Settings").ConfigureAwait(false);
            }

            // ---- Wait for the game runtime -------------------------------------------
            Report(LaunchPhase.WaitingForGame, "Waiting for the game to start…");
            var emulatorPid = await _processMonitor.WaitForGameStartAsync(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            if (emulatorPid is null)
            {
                return await FailAndRestoreAsync(snapshot, "GAME_START_TIMEOUT",
                    "The game did not start within three minutes.",
                    "Google Play Games opened but the game runtime never appeared.",
                    ct,
                    "Check Google Play Games for sign-in prompts or updates",
                    "Try launching once from Google Play Games directly").ConfigureAwait(false);
            }
            _logger.LogInformation("Game process detected (emulator PID {Pid})", emulatorPid);

            // ---- Process optimization + monitoring -------------------------------------
            var procSnapshot = await _processOptimizer.ApplyAsync(emulatorPid.Value, profile.Performance, ct).ConfigureAwait(false);
            if (procSnapshot is not null)
            {
                snapshot = snapshot with { ProcessStates = [.. snapshot.ProcessStates, procSnapshot] };
                await _recovery.UpdatePendingAsync(snapshot, ct).ConfigureAwait(false);
            }

            Report(LaunchPhase.Monitoring, "Critical Ops is running.");
            stopwatch.Start();
            var startedAt = DateTimeOffset.Now;

            if (await _metrics.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _metrics.StartAsync(emulatorPid.Value, ct).ConfigureAwait(false);
                    metricsStarted = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Frametime capture unavailable, continuing without FPS metrics");
                }
            }

            await _processMonitor.WaitForGameExitAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation("Game exited after {Duration}", stopwatch.Elapsed);

            // ---- Restore --------------------------------------------------------------
            Report(LaunchPhase.Restoring, "Restoring system settings…");
            if (metricsStarted)
            {
                await _metrics.StopAsync().ConfigureAwait(false);
            }
            await _recovery.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

            var session = new SessionRecord
            {
                ProfileName = profile.Name,
                GamePackageId = game.PackageId,
                StartedAt = startedAt,
                Duration = stopwatch.Elapsed,
                Stats = metricsStarted ? _metrics.GetSessionStats() : SessionStats.Empty,
                FpsSamples = metricsStarted ? _metrics.GetFpsSamples() : [],
            };
            await _sessionStore.SaveSessionAsync(session, CancellationToken.None).ConfigureAwait(false);

            Report(LaunchPhase.Completed, "Session complete. Settings restored.");
            return new LaunchResult { Success = true, Session = session };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Session cancelled, restoring system state");
            if (metricsStarted)
            {
                await _metrics.StopAsync().ConfigureAwait(false);
            }
            await _recovery.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Completed, "Cancelled. Settings restored.");
            return Fail("CANCELLED", "The session was cancelled.", "All temporary settings were restored.");
        }
        catch (OptimaException ex)
        {
            _logger.LogError(ex, "Session failed: {Code}", ex.Error.Code);
            await _recovery.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Failed, ex.Error.Title);
            return new LaunchResult { Success = false, Error = ex.Error };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected session failure");
            await _recovery.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Failed, "Unexpected error");
            return Fail("UNEXPECTED", "Something went wrong during the session.",
                "All temporary settings were restored. Details were written to the log.",
                "Check the Logs page for details",
                "Run Diagnostics to verify the environment");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<LaunchResult> FailAndRestoreAsync(
        SystemStateSnapshot snapshot, string code, string title, string explanation,
        CancellationToken ct, params string[] fixes)
    {
        await _recovery.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
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
        _logger.LogInformation("[{Phase}] {Message}", phase, message);
        ProgressChanged?.Invoke(this, new LaunchProgress(phase, message));
    }
}
