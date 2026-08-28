using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
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
/// Watch mode enters the same pipeline through <see cref="AttachToRunningGameAsync"/>, which
/// skips only the launch: environment changes, monitoring, restore and the session record are
/// shared, and both entry points contend for the same single-session gate.
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
    private readonly ILogger<LaunchOrchestrator> _logger;

    private int _running; // 0/1 gate so PLAY and watch mode cannot double-apply

    /// <summary>Mutable per-session state, so the catch paths always restore the latest snapshot.</summary>
    private sealed class SessionContext
    {
        public required SystemStateSnapshot Snapshot { get; set; }
        public bool MetricsStarted { get; set; }
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
        _network = network;
        _sessionStore = sessionStore;
        _tweaks = tweaks;
        _logger = logger;
    }

    public event EventHandler<LaunchProgress>? ProgressChanged;

    public bool IsSessionActive => Volatile.Read(ref _running) == 1;

    public Task<LaunchResult> RunSessionAsync(LaunchProfile profile, CancellationToken ct = default)
        => RunSessionAsync(profile, LaunchKind.Play, ct);

    public Task<LaunchResult> RunSessionAsync(LaunchProfile profile, LaunchKind kind, CancellationToken ct = default)
        => RunGatedAsync(profile, async (context, token) =>
        {
            // ---- Validate -------------------------------------------------------------
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

            // Snapshot exists on disk from here on, so any crash is recoverable.
            await _recovery.SavePendingAsync(context.Snapshot, token).ConfigureAwait(false);
            await ApplyEnvironmentAsync(profile, context, token).ConfigureAwait(false);

            // ---- Launch ---------------------------------------------------------------
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

            // ---- Wait for the game runtime -------------------------------------------
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

    /// <summary>
    /// Watch mode entry (§5): the game is already running, so this applies the full profile
    /// around the existing process, monitors it, and restores on exit. Frametime capture is
    /// started only when <paramref name="captureAllowed"/> is true; the caller passes false
    /// when the elevated helper is not already connected, so watch mode never triggers a
    /// surprise UAC prompt.
    /// </summary>
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

    /// <summary>The single-session gate plus the shared error handling and restore paths.</summary>
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

        var context = new SessionContext { Snapshot = new SystemStateSnapshot { ProfileName = profile.Name } };
        try
        {
            return await body(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Session cancelled, restoring system state");
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Completed, "Cancelled. Settings restored.");
            return Fail("CANCELLED", "The session was cancelled.", "All temporary settings were restored.");
        }
        catch (OptimaException ex)
        {
            _logger.LogError(ex, "Session failed: {Code}", ex.Error.Code);
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
            Report(LaunchPhase.Failed, ex.Error.Title);
            return new LaunchResult { Success = false, Error = ex.Error };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected session failure");
            await StopMonitoringAsync(context).ConfigureAwait(false);
            await _recovery.RestoreAsync(context.Snapshot, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Pre-game environment changes shared by PLAY and watch mode: power plan, background
    /// cleanup and the virtual display. Every mutation lands in the recovery snapshot first.
    /// </summary>
    private async Task ApplyEnvironmentAsync(LaunchProfile profile, SessionContext context, CancellationToken ct)
    {
        // ---- Performance profile --------------------------------------------------
        Report(LaunchPhase.ApplyingPerformanceProfile, "Applying performance profile…");
        if (profile.Performance.PowerPlan != PowerPlanKind.Unchanged)
        {
            var previous = await _power.ApplyAsync(profile.Performance.PowerPlan, ct).ConfigureAwait(false);
            context.Snapshot = context.Snapshot with { PreviousPowerScheme = previous };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
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

            context.Snapshot = context.Snapshot with { VirtualDisplayConfigured = true };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

            // Initialize captures the provider's restore baseline (settings backup, device state).
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

            // Capture the topology AFTER driver configuration: a driver reload re-creates the
            // virtual display with new CCD target ids, so an earlier snapshot would reference
            // targets that no longer exist and could not be re-applied.
            var topology = await _displayService.CaptureTopologyAsync(ct).ConfigureAwait(false);
            context.Snapshot = context.Snapshot with { DisplayTopology = topology };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);

            if (profile.Display.MakePrimary
                && await _virtualDisplay.GetDisplayInfoAsync(ct).ConfigureAwait(false) is { } displayInfo)
            {
                // Opt-in only: on a local machine the virtual display is invisible, so making
                // it primary is for capture/streaming setups. Topology restore reverts it.
                await _displayService.MakePrimaryAsync(displayInfo.DeviceName, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The shared session tail: process optimization, monitoring, exit wait, restore and the
    /// session record. Identical for PLAY, watch and benchmark sessions.
    /// </summary>
    private async Task<LaunchResult> MonitorAndCompleteAsync(
        LaunchProfile profile, string packageId, int emulatorPid,
        SessionContext context, LaunchKind kind, bool captureAllowed, CancellationToken ct)
    {
        // ---- Process optimization + monitoring -------------------------------------
        var procSnapshot = await _processOptimizer.ApplyAsync(emulatorPid, profile.Performance, ct).ConfigureAwait(false);
        if (procSnapshot is not null)
        {
            context.Snapshot = context.Snapshot with { ProcessStates = [.. context.Snapshot.ProcessStates, procSnapshot] };
            await _recovery.UpdatePendingAsync(context.Snapshot, ct).ConfigureAwait(false);
        }

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

        await _processMonitor.WaitForGameExitAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();
        _logger.LogInformation("Game exited after {Duration}", stopwatch.Elapsed);

        // ---- Restore --------------------------------------------------------------
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
        var sessionId = await _sessionStore.SaveSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        session = session with { Id = sessionId };

        Report(LaunchPhase.Completed, "Session complete. Settings restored.");
        return new LaunchResult { Success = true, Session = session };
    }

    /// <summary>Stops metrics + network on the failure paths; never throws.</summary>
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

    /// <summary>Best-effort network stop; measurement problems never fail a session.</summary>
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

    /// <summary>Enabled tweak ids at session end, best-effort: history context, never a failure.</summary>
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

    /// <summary>
    /// Every game-related pid is a capture candidate: the DXGI presenter is not always the
    /// emulator process, so the collector gets platform, emulator and game-window pids and
    /// reports whichever one actually presents frames.
    /// </summary>
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
        _logger.LogInformation("[{Phase}] {Message}", phase, message);
        ProgressChanged?.Invoke(this, new LaunchProgress(phase, message));
    }
}
