using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Core.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class LaunchOrchestratorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-orch-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);

    private readonly FakeDetector _detector = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeVirtualDisplay _virtualDisplay = new();
    private readonly FakeDisplayService _displayService = new();
    private readonly FakePowerService _power = new();
    private readonly FakeProcessMonitor _processMonitor = new();
    private readonly FakeProcessOptimizer _processOptimizer = new();
    private readonly FakeCleanup _cleanup = new();
    private readonly FakeMetrics _metrics = new();
    private readonly FakeNetworkMonitor _network = new();
    private readonly FakeSessionStore _sessionStore = new();
    private readonly FakeTweakService _tweaks = new();

    public LaunchOrchestratorTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private RecoveryService CreateRecovery() => new(
        _paths, _store, _displayService, _power, _virtualDisplay, _processOptimizer,
        NullLogger<RecoveryService>.Instance);

    private LaunchOrchestrator CreateOrchestrator(
        SettingsService? settings = null, FakeLaunchSupport? support = null, FakeElevationBroker? elevation = null,
        FakeDriverInstaller? driver = null) => new(
        _detector, [_launcher], _virtualDisplay, _displayService, _power,
        _processMonitor, _processOptimizer, _cleanup, CreateRecovery(), _metrics, _network, _sessionStore, _tweaks,
        NullLogger<LaunchOrchestrator>.Instance, settings, support, elevation, driver);

    private static OptimaException NoDisplay() => OptimaException.From(
        "VDD_NO_DISPLAY", "The virtual display did not appear.", "Windows never attached its display to the desktop.");

    private SettingsService SettingsWithPriority(string priority)
    {
        var settings = new SettingsService(_paths, _store, NullLogger<SettingsService>.Instance);
        settings.SaveSettingsAsync(new AppSettings { GamePriority = priority }).GetAwaiter().GetResult();
        return settings;
    }

    private static LaunchProfile CompetitiveProfile => new()
    {
        Name = "Test Competitive",
        Display = new DisplayProfile { VirtualDisplay = true, Width = 1920, Height = 1080, RefreshRate = 240 },
        Performance = new PerformanceProfile
        {
            PowerPlan = PowerPlanKind.HighPerformance,
            Priority = ProcessPriorityLevel.High,
            DisablePowerThrottling = true,
        },
    };

    [Fact]
    public async Task RunSession_HappyPath_AppliesEverythingAndRestores()
    {
        _metrics.Available = true;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.Equal(200, result.Session.Stats.AverageFps);

        // Power applied then restored.
        Assert.Contains("apply:HighPerformance", _power.Log);
        Assert.Contains("restore", _power.Log);

        // Virtual display enabled, mode set, then original state restored and the display switched off.
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Contains("mode:1920x1080 @ 240 Hz", _virtualDisplay.Log);
        Assert.Contains("restoreOriginal", _virtualDisplay.Log);
        Assert.Contains("disable", _virtualDisplay.Log);
        Assert.False(_virtualDisplay.Active);

        // Topology captured before changes and restored after.
        Assert.Contains("capture", _displayService.Log);
        Assert.Contains("restoreTopology", _displayService.Log);

        // Emulator process tuned then restored; metrics ran; session persisted.
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Equal([4242], _processOptimizer.Restored);
        Assert.True(_metrics.Started);
        Assert.True(_metrics.Stopped);
        Assert.Single(_sessionStore.Saved);

        // No pending snapshot left behind.
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_PlatformMissing_FailsWithFriendlyError()
    {
        _detector.Platform = null;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("GPG_NOT_FOUND", result.Error?.Code);
        Assert.NotEmpty(result.Error!.SuggestedFixes);
    }

    [Fact]
    public async Task RunSession_GameMissing_FailsWithFriendlyError()
    {
        _detector.Game = null;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("GAME_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task RunSession_AllLaunchersFail_RestoresEverything()
    {
        _launcher.LaunchSucceeds = false;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("LAUNCH_FAILED", result.Error?.Code);
        Assert.Contains("restore", _power.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
        // No game, so the virtual display was never touched.
        Assert.Empty(_virtualDisplay.Log);
        Assert.Empty(_displayService.Log);
    }

    [Fact]
    public async Task RunSession_GameNeverStarts_TimesOutAndRestores()
    {
        _processMonitor.GameStartPid = null;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("GAME_START_TIMEOUT", result.Error?.Code);
        Assert.Contains("restore", _power.Log);
        Assert.Empty(_virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_PowerPlanUnavailable_StillStartsTheGameWithAWarning()
    {
        _power.ApplyError = OptimaException.From(
            "POWER_PLAN_UNAVAILABLE", "This PC does not offer that power plan", "Windows does not list it on this PC.");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("POWER_PLAN_UNAVAILABLE", warning.Code);
        Assert.Equal("This PC does not offer that power plan", warning.Title);

        // The rest of the profile still went on, and the game was launched and recorded.
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Equal(1, _launcher.LaunchCalls);
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Single(_sessionStore.Saved);

        // No plan was switched, so there is none to put back.
        Assert.DoesNotContain("restore", _power.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_PowerCallFailsOutright_TheWarningCarriesWhatWindowsReported()
    {
        _power.ApplyError = new System.ComponentModel.Win32Exception(5, "PowerSetActiveScheme failed");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("LAUNCH_STEP_SKIPPED", warning.Code);
        Assert.Contains("PowerSetActiveScheme failed (Win32 5", warning.Detail);
    }

    [Fact]
    public async Task RunSession_UnexpectedFailure_NamesThePhaseAndCarriesTheException()
    {
        _processMonitor.ExitError = new InvalidOperationException("the process list went away");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("UNEXPECTED", result.Error?.Code);
        Assert.Contains("the game was running", result.Error!.Explanation);
        Assert.StartsWith("phase: Monitoring", result.Error.DeveloperDetails);
        Assert.Contains("System.InvalidOperationException: the process list went away", result.Error.DeveloperDetails);

        // And everything came off again, the virtual display that was on under the game too.
        Assert.Contains("restore", _power.Log);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.False(_virtualDisplay.Active);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_TheDisplayFailsUnderTheRunningGame_TheSessionCarriesOnWithAWarning()
    {
        _virtualDisplay.EnableError = new InvalidOperationException("the driver went away");
        var reports = new List<LaunchProgress>();
        var orchestrator = CreateOrchestrator();
        orchestrator.ProgressChanged += (_, progress) => reports.Add(progress);

        var result = await orchestrator.RunSessionAsync(CompetitiveProfile);

        // The game is up by the time the display is tried, so nothing is put back under it.
        Assert.True(result.Success);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("LAUNCH_STEP_SKIPPED", warning.Code);
        Assert.Equal("The virtual display was not set up", warning.Title);
        Assert.Contains("the driver went away", warning.Detail);
        Assert.Contains(reports, progress => progress is { Phase: LaunchPhase.ConfiguringDisplay, Skipped: true });
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Single(_sessionStore.Saved);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.False(_virtualDisplay.Active);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_TheModeIsRefusedAfterTheDisplayCameOn_ItStillGoesOffAtExit()
    {
        _virtualDisplay.ModeError = OptimaException.From(
            "VDD_MODE_NOT_APPLIED", "The virtual display did not take that mode.", "The driver kept its own.");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal("VDD_MODE_NOT_APPLIED", Assert.Single(result.Warnings).Code);
        // It was on while the game ran, and the restore at exit switched it off.
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Single(_virtualDisplay.Log, entry => entry == "disable");
        Assert.False(_virtualDisplay.Active);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_TheDisplayComesOnOnlyOnceTheGameIsSeenRunning()
    {
        (int Launches, int GameStartWaits, int Tuned, bool Watching)? atEnable = null;
        _virtualDisplay.OnEnable = () => atEnable =
            (_launcher.LaunchCalls, _processMonitor.GameStartWaits, _processOptimizer.Applied.Count, _network.Started);

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        // After the launcher and after the game was found; before the game is tuned and watched.
        Assert.True(result.Success);
        Assert.Equal((1, 1, 0, false), atEnable);
    }

    [Fact]
    public async Task RunSession_OptimaKilledWhileTheDisplayComesOn_TheNextStartSwitchesItOff()
    {
        SystemStateSnapshot? onDisk = null;
        var recovery = CreateRecovery();
        _virtualDisplay.OnEnable = () => onDisk = recovery.GetPendingAsync().GetAwaiter().GetResult();

        Assert.True((await CreateOrchestrator().RunSessionAsync(CompetitiveProfile)).Success);

        // What the pending file said while the device was being switched on, restored by the next
        // start's fresh provider over a display the dead session left on.
        Assert.True(onDisk!.VirtualDisplayEnabledByUs);
        var afterKill = new FakeVirtualDisplay { Active = true };
        await new RecoveryService(_paths, _store, _displayService, _power, afterKill, _processOptimizer,
            NullLogger<RecoveryService>.Instance).RestoreAsync(onDisk);
        Assert.False(afterKill.Active);
    }

    [Fact]
    public async Task RunSession_TheDisplaysAdministratorPromptComesBeforeTheGameStarts()
    {
        var elevation = new FakeElevationBroker { ConnectOnPrompt = true };
        int? launchesAtPrompt = null;
        elevation.OnPrompt = () => launchesAtPrompt = _launcher.LaunchCalls;

        var result = await CreateOrchestrator(elevation: elevation).RunSessionAsync(CompetitiveProfile);

        // Asked once, before the launch, and the display then comes on under the running game
        // without asking again.
        Assert.True(result.Success);
        Assert.Equal(1, elevation.Prompts);
        Assert.Equal(0, launchesAtPrompt);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task RunSession_TheAdministratorPromptIsDeclined_TheDisplayDoesNotAskAgainOverTheGame()
    {
        var elevation = new FakeElevationBroker();

        var result = await CreateOrchestrator(elevation: elevation).RunSessionAsync(CompetitiveProfile);

        // The game plays on without the display, and nothing of the driver is touched, since any of
        // it could put the prompt up again.
        Assert.True(result.Success);
        Assert.Equal("ELEVATION_DECLINED", Assert.Single(result.Warnings).Code);
        Assert.Equal(1, elevation.Prompts);
        Assert.Empty(_virtualDisplay.Log);
        Assert.Single(_sessionStore.Saved);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_TheHelperIsAlreadyThere_NothingIsAsked()
    {
        var elevation = new FakeElevationBroker { IsConnected = true };

        var result = await CreateOrchestrator(elevation: elevation).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.Contains("enable", _virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_ThePromptIsStillOpenOnceTheGameIsUp_TheGameIsNotHeldUpForIt()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elevation = new FakeElevationBroker { Answer = answer.Task };
        var orchestrator = CreateOrchestrator(elevation: elevation);
        orchestrator.DisplayHelperWait = TimeSpan.FromMilliseconds(50);

        var result = await orchestrator.RunSessionAsync(CompetitiveProfile).WaitAsync(TimeSpan.FromSeconds(10));

        // The game was tuned and watched to its exit, and the display stayed off and says why.
        Assert.True(result.Success);
        Assert.Equal("ELEVATION_DECLINED", Assert.Single(result.Warnings).Code);
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Single(_sessionStore.Saved);
        Assert.False(orchestrator.IsSessionActive);
        // A yes after the game has gone does not switch the display on.
        answer.SetResult(true);
        Assert.Empty(_virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_TheDriverIsNotInstalled_NothingIsAskedAndTheMissingDriverIsTheReason()
    {
        // The prompt would be answered with No; it must not even be put up.
        var elevation = new FakeElevationBroker();
        var driver = new FakeDriverInstaller { State = DriverState.NotInstalledPackageAvailable };

        var result = await CreateOrchestrator(elevation: elevation, driver: driver).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.Equal("VDD_NOT_INSTALLED", Assert.Single(result.Warnings).Code);
        Assert.Empty(_virtualDisplay.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_AProviderThatNeedsNoAdministrator_IsNeitherAskedForNorStopped()
    {
        // The developer's stand-in, which runs without the driver and without the helper.
        _virtualDisplay.RequiresElevation = false;
        var elevation = new FakeElevationBroker();
        var driver = new FakeDriverInstaller { State = DriverState.NotInstalledNoPackage };

        var result = await CreateOrchestrator(elevation: elevation, driver: driver).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task RunSession_WithoutTheVirtualDisplay_NoPromptIsAskedForIt()
    {
        var elevation = new FakeElevationBroker();
        var profile = CompetitiveProfile with { Display = new DisplayProfile { VirtualDisplay = false } };

        var result = await CreateOrchestrator(elevation: elevation).RunSessionAsync(profile);

        Assert.True(result.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.Empty(_virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_ReportsTheDisplayBetweenTheGameStartingAndTheGameBeingWatched()
    {
        var phases = new List<LaunchPhase>();
        var orchestrator = CreateOrchestrator();
        orchestrator.ProgressChanged += (_, progress) => phases.Add(progress.Phase);

        await orchestrator.RunSessionAsync(CompetitiveProfile);

        Assert.Equal(
        [
            LaunchPhase.Validating, LaunchPhase.ApplyingPerformanceProfile, LaunchPhase.StartingPlatform,
            LaunchPhase.WaitingForGame, LaunchPhase.ConfiguringDisplay, LaunchPhase.Monitoring,
            LaunchPhase.Restoring, LaunchPhase.Completed,
        ], phases);
    }

    [Fact]
    public async Task RunSession_TheDisplayIsOnWhileTheGameRunsAndOffOnceItsProcessIsGone()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMinutes(1);

        var session = CreateOrchestrator().RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _network.Started);
        Assert.True(_virtualDisplay.Active);
        // What the monitor's wait does when the emulator process is killed: it returns.
        _processMonitor.EndGameNow();
        var result = await session;

        Assert.True(result.Success);
        Assert.Single(_sessionStore.Saved);
        Assert.False(_virtualDisplay.Active);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_ProcessTuningFails_TheRunningGameKeepsItsSession()
    {
        _processOptimizer.ApplyError = new InvalidOperationException("the process refused the priority");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal("LAUNCH_STEP_SKIPPED", Assert.Single(result.Warnings).Code);
        Assert.Single(_sessionStore.Saved);
        Assert.Contains("restore", _power.Log);
    }

    [Fact]
    public async Task RunSession_SessionSaveFails_RestoresOnlyOnce()
    {
        _sessionStore.SaveError = new IOException("the database is locked");

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        // The game ran and everything was put back; only the history entry is missing.
        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.Equal("SESSION_NOT_SAVED", Assert.Single(result.Warnings).Code);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Single(_virtualDisplay.Log, entry => entry == "restoreOriginal");
    }

    [Fact]
    public async Task RunSession_CaptureStopNeverAnswers_StillSavesTheSession()
    {
        _metrics.Available = true;
        // What a helper that does not answer the stop in time surfaces as.
        _metrics.StopError = new TaskCanceledException();

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        // The game was played and has ended: that is a session, not a cancelled launch.
        Assert.True(result.Success);
        Assert.Single(_sessionStore.Saved);
        Assert.True(_network.Stopped);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_VirtualDisplayMissing_ReloadsOnceAndContinues()
    {
        var support = new FakeLaunchSupport { Repairable = { "VDD_NO_DISPLAY" } };
        _virtualDisplay.EnableErrors.Enqueue(NoDisplay());

        var result = await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        // The display did not come up, the driver was reloaded, and the second try brought it up.
        Assert.True(result.Success);
        Assert.Equal(["VDD_NO_DISPLAY"], support.Repaired);
        Assert.Equal(2, _virtualDisplay.Log.Count(entry => entry == "enable"));
        Assert.Contains("mode:1920x1080 @ 240 Hz", _virtualDisplay.Log);
        Assert.Equal(1, _launcher.LaunchCalls);
    }

    [Fact]
    public async Task RunSession_VirtualDisplayStillMissing_TheGameKeepsItsSessionUnderTheTypedCode()
    {
        var support = new FakeLaunchSupport { Repairable = { "VDD_NO_DISPLAY" } };
        _virtualDisplay.EnableErrors.Enqueue(NoDisplay());
        _virtualDisplay.EnableErrors.Enqueue(NoDisplay());

        var result = await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        // One repair, one more try, and then the warning the step itself gave, not a generic one.
        // The game was already running, so the session goes on without the display.
        Assert.True(result.Success);
        Assert.Equal("VDD_NO_DISPLAY", Assert.Single(result.Warnings).Code);
        Assert.Single(support.Repaired);
        Assert.Equal(2, _virtualDisplay.Log.Count(entry => entry == "enable"));
        Assert.Equal(1, _launcher.LaunchCalls);
        Assert.Single(_sessionStore.Saved);
        Assert.Contains("restore", _power.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_AFaultNoRepairIsKnownFor_IsNotTriedASecondTime()
    {
        var support = new FakeLaunchSupport();
        _virtualDisplay.EnableErrors.Enqueue(OptimaException.From(
            "DEVICE_TOGGLE_FAILED", "Windows refused to change the virtual display device.", "pnputil said no."));

        var result = await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal("DEVICE_TOGGLE_FAILED", Assert.Single(result.Warnings).Code);
        Assert.Empty(support.Repaired);
        Assert.Single(_virtualDisplay.Log, entry => entry == "enable");
    }

    [Fact]
    public async Task RunSession_WithTheVirtualDisplayOnAndNoDriver_PlaysOnTheRealScreenAndSaysSo()
    {
        _virtualDisplay.EnableError = OptimaException.From(
            "VDD_NOT_INSTALLED", "No virtual display driver device was found.", "It is not in Device Manager.");

        var reports = new List<LaunchProgress>();
        var orchestrator = CreateOrchestrator();
        orchestrator.ProgressChanged += (_, progress) => reports.Add(progress);

        var result = await orchestrator.RunSessionAsync(CompetitiveProfile);

        // A driver nobody installed is not a reason to stop the game.
        Assert.True(result.Success);
        Assert.Equal("VDD_NOT_INSTALLED", Assert.Single(result.Warnings).Code);
        Assert.Contains(reports, progress => progress is { Phase: LaunchPhase.ConfiguringDisplay, Skipped: true });
        Assert.DoesNotContain(_virtualDisplay.Log, entry => entry.StartsWith("mode:", StringComparison.Ordinal));
        // The rest of the profile still applied.
        Assert.Contains("apply:HighPerformance", _power.Log);
    }

    [Fact]
    public async Task RunSession_TheDisplayIsTheDisplayPagesChoice_NotTheProfiles()
    {
        var settings = new SettingsService(_paths, _store, NullLogger<SettingsService>.Instance);
        await settings.SaveSettingsAsync(new AppSettings
        {
            VirtualDisplayEnabled = true,
            VirtualDisplayWidth = 2560,
            VirtualDisplayHeight = 1440,
            VirtualDisplayRefreshRate = 165,
        });
        // A profile that says nothing about a virtual display, which is every built-in one now.
        var profile = CompetitiveProfile with { Display = new DisplayProfile { VirtualDisplay = false } };

        var result = await CreateOrchestrator(settings).RunSessionAsync(profile);

        Assert.True(result.Success);
        Assert.Contains("mode:2560x1440 @ 165 Hz", _virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_TheDisplayPagesChoiceCanAlsoBeOff()
    {
        var settings = new SettingsService(_paths, _store, NullLogger<SettingsService>.Instance);
        await settings.SaveSettingsAsync(new AppSettings { VirtualDisplayEnabled = false });

        // The profile still carries the display it was saved with before the choice moved.
        var result = await CreateOrchestrator(settings).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.DoesNotContain("enable", _virtualDisplay.Log);
    }

    [Fact]
    public async Task RunSession_ChecksRunBeforeAnythingIsChanged()
    {
        var changedByThen = -1;
        var support = new FakeLaunchSupport();
        support.OnPreflight = () => changedByThen = _power.Log.Count + _virtualDisplay.Log.Count + _cleanup.Closed.Count;

        var result = await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(1, support.Preflights);
        Assert.Equal(0, changedByThen);
    }

    [Fact]
    public async Task RunSession_ChecksThatFail_NeverStopALaunch()
    {
        var support = new FakeLaunchSupport { PreflightError = new InvalidOperationException("WMI is not answering") };

        var result = await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task RunSession_ThatIsStoppedAtValidation_RunsNoChecks()
    {
        var support = new FakeLaunchSupport();
        _detector.Game = null;

        await CreateOrchestrator(support: support).RunSessionAsync(CompetitiveProfile);

        // Nothing is about to be changed, so there is nothing to check ahead of.
        Assert.Equal(0, support.Preflights);
    }

    [Fact]
    public async Task RunSession_NothingSkipped_CarriesNoWarnings()
    {
        var reports = new List<LaunchProgress>();
        var orchestrator = CreateOrchestrator();
        orchestrator.ProgressChanged += (_, progress) => reports.Add(progress);

        var result = await orchestrator.RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(reports, progress => progress.Skipped);
    }

    [Fact]
    public async Task RunSession_SecondConcurrentStart_IsRejected()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMilliseconds(600);
        var orchestrator = CreateOrchestrator();

        var first = orchestrator.RunSessionAsync(CompetitiveProfile);
        await Task.Delay(150);
        var second = await orchestrator.RunSessionAsync(CompetitiveProfile);

        Assert.Equal("SESSION_ACTIVE", second.Error?.Code);
        var firstResult = await first;
        Assert.True(firstResult.Success);
    }

    [Fact]
    public async Task RunSession_DefaultProfile_TouchesNothing()
    {
        var profile = new LaunchProfile { Name = "Default" };

        var result = await CreateOrchestrator().RunSessionAsync(profile);

        Assert.True(result.Success);
        Assert.Empty(_power.Log);
        Assert.Empty(_virtualDisplay.Log);
        Assert.Empty(_displayService.Log);
    }

    [Fact]
    public async Task RunSession_CleanupList_OnlyClosesListedProcesses()
    {
        var profile = new LaunchProfile
        {
            Name = "Cleanup",
            Performance = new PerformanceProfile { CleanupProcessNames = ["Discord", "SomeUpdater"] },
        };

        await CreateOrchestrator().RunSessionAsync(profile);

        Assert.Equal(["Discord", "SomeUpdater"], _cleanup.Closed);
    }

    [Fact]
    public async Task Attach_HappyPath_AppliesProfileMonitorsAndRestores()
    {
        _metrics.Available = true;

        var result = await CreateOrchestrator().AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: true);

        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.Equal(LaunchKind.Watch, result.Session.LaunchKind);

        // The full profile went on and came off again, without any launch. The game is already
        // running, so the virtual display comes on at once.
        Assert.Contains("apply:HighPerformance", _power.Log);
        Assert.Contains("restore", _power.Log);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Contains("restoreOriginal", _virtualDisplay.Log);
        Assert.False(_virtualDisplay.Active);
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Equal([4242], _processOptimizer.Restored);
        Assert.True(_metrics.Started);
        Assert.Single(_sessionStore.Saved);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task Attach_APromptLeftOpen_HoldsTheGameNoLongerThanPlayDoes()
    {
        // A prompt that stays open: before the fix the session waited on it with the game up.
        var elevation = new FakeElevationBroker { Answer = new TaskCompletionSource<bool>().Task };
        var phases = new List<LaunchPhase>();
        var orchestrator = CreateOrchestrator(elevation: elevation);
        orchestrator.DisplayHelperWait = TimeSpan.FromMilliseconds(100);
        orchestrator.ProgressChanged += (_, progress) => phases.Add(progress.Phase);

        var result = await orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Asked once, given up on after the wait, nothing of the driver touched, and the game is
        // tuned and watched as usual.
        Assert.True(result.Success);
        Assert.Equal(1, elevation.Prompts);
        Assert.Equal("ELEVATION_DECLINED", Assert.Single(result.Warnings).Code);
        Assert.Empty(_virtualDisplay.Log);
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Contains(LaunchPhase.Monitoring, phases);
    }

    [Fact]
    public async Task Attach_TheHelperIsNotRunning_AYesSwitchesTheDisplayOn()
    {
        // The display follows the game also when watch mode found it: the prompt is the price of that.
        var elevation = new FakeElevationBroker { ConnectOnPrompt = true };

        var result = await CreateOrchestrator(elevation: elevation).AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);

        Assert.True(result.Success);
        Assert.Equal(1, elevation.Prompts);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Attach_AfterThePlayerSaidNoThisRun_DoesNotAskAgainOverTheGame()
    {
        // The No came earlier in this run, at PLAY or at an earlier game watch mode found.
        var elevation = new FakeElevationBroker { LastStartFailure = ElevationStartFailure.Declined };
        var orchestrator = CreateOrchestrator(elevation: elevation);

        var first = await orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);
        var later = await orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4243, captureAllowed: false);

        Assert.True(first.Success);
        Assert.True(later.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.DoesNotContain("enable", _virtualDisplay.Log);
    }

    [Fact]
    public async Task Play_AfterANoThisRun_MayStillAsk()
    {
        var elevation = new FakeElevationBroker { LastStartFailure = ElevationStartFailure.Declined, ConnectOnPrompt = true };

        var result = await CreateOrchestrator(elevation: elevation).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(1, elevation.Prompts);
    }

    [Fact]
    public async Task Attach_TheHelperIsAlreadyRunning_TheDisplayComesOn()
    {
        var elevation = new FakeElevationBroker { IsConnected = true };

        var result = await CreateOrchestrator(elevation: elevation).AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);

        Assert.True(result.Success);
        Assert.Equal(0, elevation.Prompts);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Attach_CaptureNotAllowed_RecordsSessionWithoutMetrics()
    {
        _metrics.Available = true;

        var result = await CreateOrchestrator().AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);

        Assert.True(result.Success);
        Assert.False(_metrics.Started);
        var saved = Assert.Single(_sessionStore.Saved);
        Assert.False(saved.Stats.HasData);
    }

    [Fact]
    public async Task Attach_WhilePlaySessionRuns_IsRejected()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMilliseconds(600);
        var orchestrator = CreateOrchestrator();

        var play = orchestrator.RunSessionAsync(CompetitiveProfile);
        await Task.Delay(150);
        var attach = await orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: true);

        Assert.Equal("SESSION_ACTIVE", attach.Error?.Code);
        Assert.True((await play).Success);
    }

    [Fact]
    public async Task RunSession_WhileAttached_IsRejected()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMilliseconds(600);
        var orchestrator = CreateOrchestrator();

        var attach = orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);
        await Task.Delay(150);
        var play = await orchestrator.RunSessionAsync(CompetitiveProfile);

        Assert.Equal("SESSION_ACTIVE", play.Error?.Code);
        Assert.True((await attach).Success);
    }

    [Fact]
    public async Task RunSession_SettingsGamePriority_OverridesEveryProfile()
    {
        var result = await CreateOrchestrator(SettingsWithPriority("AboveNormal")).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.AboveNormal, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task RunSession_SettingsGamePriorityNormal_OverridesEveryProfile()
    {
        var result = await CreateOrchestrator(SettingsWithPriority("Normal")).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.Normal, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task RunSession_SettingsGamePriorityUnchanged_KeepsProfilePriority()
    {
        var result = await CreateOrchestrator(SettingsWithPriority("Unchanged")).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.High, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task RunSession_UnknownGamePriority_KeepsProfilePriority()
    {
        var result = await CreateOrchestrator(SettingsWithPriority("definitely-not-a-priority")).RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.High, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task RunSession_NoSettingsAtAll_KeepsProfilePriority()
    {
        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.High, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task Attach_SettingsGamePriority_IsAppliedToo()
    {
        var result = await CreateOrchestrator(SettingsWithPriority("AboveNormal"))
            .AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.AboveNormal, Assert.Single(_processOptimizer.AppliedProfiles).Priority);
    }

    [Fact]
    public async Task RunSession_KeepsReassertingPriorityUntilTheGameExits()
    {
        _processMonitor.ExitAfter = TimeSpan.FromSeconds(5);
        var orchestrator = CreateOrchestrator();
        orchestrator.PriorityKeeperInterval = TimeSpan.FromMilliseconds(20);

        var session = orchestrator.RunSessionAsync(CompetitiveProfile);
        // The first pass is recorded before it waits on the gate, so the wait cannot race it.
        await WaitForAsync(() => _processOptimizer.Reasserted.Count >= 1);
        Assert.Single(_processOptimizer.Reasserted);
        Assert.Equal([4242], _processOptimizer.Reasserted);
        Assert.Equal(ProcessPriorityLevel.High, _processOptimizer.ReassertedProfiles[0].Priority);

        // Releasing the gate lets each pass finish; the keeper must keep going for the whole
        // session, not stop after correcting once.
        _processOptimizer.ReleaseGate();
        await WaitForAsync(() => _processOptimizer.Reasserted.Count >= 3);

        _processMonitor.EndGameNow();
        var result = await session;
        Assert.True(result.Success);
        Assert.True(_processOptimizer.Reasserted.Count >= 3);
        Assert.Equal([4242], _processOptimizer.Restored);
    }

    [Fact]
    public async Task RunSession_PriorityKeeper_UsesTheSettingsPriority()
    {
        _processMonitor.ExitAfter = TimeSpan.FromSeconds(5);
        var orchestrator = CreateOrchestrator(SettingsWithPriority("AboveNormal"));
        orchestrator.PriorityKeeperInterval = TimeSpan.FromMilliseconds(20);

        var session = orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _processOptimizer.ReassertedProfiles.Count >= 1);

        Assert.Equal(ProcessPriorityLevel.AboveNormal, _processOptimizer.ReassertedProfiles[0].Priority);

        _processOptimizer.ReleaseGate();
        _processMonitor.EndGameNow();
        Assert.True((await session).Success);
    }

    [Fact]
    public async Task Attach_PriorityKeeper_RunsForTheAttachedSessionToo()
    {
        _processMonitor.ExitAfter = TimeSpan.FromSeconds(5);
        var orchestrator = CreateOrchestrator();
        orchestrator.PriorityKeeperInterval = TimeSpan.FromMilliseconds(20);

        var attach = orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false);
        await WaitForAsync(() => _processOptimizer.Reasserted.Count >= 1);
        Assert.Equal([4242], _processOptimizer.Reasserted);

        _processOptimizer.ReleaseGate();
        _processMonitor.EndGameNow();
        Assert.True((await attach).Success);
    }

    [Fact]
    public async Task RunSession_ProfileWithoutProcessTuning_StartsNoKeeper()
    {
        _processOptimizer.ReturnSnapshot = false;
        _processMonitor.ExitAfter = TimeSpan.FromMilliseconds(200);
        var orchestrator = CreateOrchestrator();
        orchestrator.PriorityKeeperInterval = TimeSpan.FromMilliseconds(20);

        var result = await orchestrator.RunSessionAsync(CompetitiveProfile);

        // Nothing was applied, so there is nothing to keep correcting.
        Assert.True(result.Success);
        Assert.Empty(_processOptimizer.Reasserted);
    }

    [Fact]
    public async Task Stop_WhileTheGameRuns_EndsTheSessionAndSavesIt()
    {
        _metrics.Available = true;
        _processMonitor.ExitAfter = TimeSpan.FromMinutes(1);
        var orchestrator = CreateOrchestrator();

        var session = orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _network.Started);
        var ended = await orchestrator.StopAsync(TimeSpan.FromSeconds(10));
        var result = await session;

        // The game is still up, and the session ended as if it had exited: recorded, not cancelled.
        Assert.True(ended);
        Assert.False(orchestrator.IsSessionActive);
        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.Single(_sessionStore.Saved);
        Assert.True(_metrics.Stopped);
        Assert.True(_network.Stopped);

        // Everything was put back, once, and no snapshot is left to ask about at the next start. The
        // virtual display goes off although the game is still up.
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Single(_virtualDisplay.Log, entry => entry == "restoreOriginal");
        Assert.Single(_virtualDisplay.Log, entry => entry == "disable");
        Assert.False(_virtualDisplay.Active);
        Assert.Equal([4242], _processOptimizer.Restored);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task Stop_BeforeTheGameIsUp_CancelsAndRestores()
    {
        _processMonitor.GameStillLoading = true;
        var orchestrator = CreateOrchestrator();
        LaunchProgress? last = null;
        orchestrator.ProgressChanged += (_, progress) => last = progress;

        var session = orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _launcher.LaunchCalls == 1);
        var ended = await orchestrator.StopAsync(TimeSpan.FromSeconds(10));
        // Checked before the session is awaited: a stop that did not end it must fail here, not
        // leave the test waiting on a session that never returns.
        Assert.True(ended);
        var result = await session;

        // Nothing was played, so there is nothing to record; the half-applied profile came off, and
        // the virtual display, which waits for the game, was never switched on.
        Assert.Equal("CANCELLED", result.Error?.Code);
        // The end is reported as a cancel, so the step list does not tick off the wait for the game.
        Assert.Equal(new LaunchProgress(LaunchPhase.Completed, "Cancelled. Settings restored.", Skipped: true), last);
        Assert.Empty(_sessionStore.Saved);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Empty(_virtualDisplay.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task Stop_TheSessionItEnded_CannotBeAttachedToAgain()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMinutes(1);
        var orchestrator = CreateOrchestrator();
        // What watch mode does when a session ends with the game still on screen: one attach per run.
        Task<LaunchResult>? attach = null;
        orchestrator.SessionEnded += () =>
            attach ??= Task.Run(() => orchestrator.AttachToRunningGameAsync(CompetitiveProfile, 4242, captureAllowed: false));

        var session = orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _network.Started);
        await orchestrator.StopAsync(TimeSpan.FromSeconds(10));

        Assert.True((await session).Success);
        Assert.NotNull(attach);
        Assert.False((await attach).Success);
        // The profile went on once and came off once, and stayed off.
        Assert.Single(_processOptimizer.Applied);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Single(_sessionStore.Saved);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task Stop_WithNoSession_ReturnsAtOnceAndRefusesTheNextOne()
    {
        var orchestrator = CreateOrchestrator();
        var sessionsEnded = 0;
        orchestrator.SessionEnded += () => sessionsEnded++;

        Assert.True(await orchestrator.StopAsync(TimeSpan.FromSeconds(10)));
        var result = await orchestrator.RunSessionAsync(CompetitiveProfile);

        // Turned away at the door: no session ran, so none ended and nothing was restored.
        Assert.False(result.Success);
        Assert.Equal(0, sessionsEnded);
        Assert.Equal(0, _launcher.LaunchCalls);
        Assert.Empty(_power.Log);
    }

    [Fact]
    public async Task RunSession_CancelledWhileTheGameRuns_RestoresAndRecordsNothing()
    {
        _processMonitor.ExitAfter = TimeSpan.FromMinutes(1);
        using var cancel = new CancellationTokenSource();

        var session = CreateOrchestrator().RunSessionAsync(CompetitiveProfile, cancel.Token);
        await WaitForAsync(() => _network.Started);
        await cancel.CancelAsync();
        var result = await session;

        // The Cancel button: a stop asked for by the player is not a session to keep. The virtual
        // display that came on with the game goes off with the rest.
        Assert.Equal("CANCELLED", result.Error?.Code);
        Assert.Empty(_sessionStore.Saved);
        Assert.Single(_power.Log, entry => entry == "restore");
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.False(_virtualDisplay.Active);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    private async Task<GameWatchService> StartWatchModeAsync(LaunchOrchestrator orchestrator, GamePresenceService presence)
    {
        _processMonitor.Tracked = [new TrackedProcess { ProcessId = 4242, Name = "crosvm", Kind = TrackedProcessKind.Emulator }];
        var settings = new SettingsService(_paths, _store, NullLogger<SettingsService>.Instance);
        await settings.SaveSettingsAsync(new AppSettings { EnableWatchMode = true });
        var watch = new GameWatchService(
            presence, _processMonitor, orchestrator, settings,
            new ProfileService(_paths, _store, NullLogger<ProfileService>.Instance),
            new FakeElevationBroker(), NullLogger<GameWatchService>.Instance);
        await watch.StartAsync();
        return watch;
    }

    [Fact]
    public async Task WatchMode_ASessionEndsBecauseTheGameClosed_IsNotAttachedToAgain()
    {
        // The window has gone and the emulator is still up. Presence only drops "in game" three
        // polls later, so for those seconds it still tells of a running game.
        _processMonitor.GameState = GameRuntimeState.Starting;
        var presence = new GamePresenceService(_processMonitor, NullLogger<GamePresenceService>.Instance);
        presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var orchestrator = CreateOrchestrator();
        await using var watch = await StartWatchModeAsync(orchestrator, presence);
        var attached = false;
        watch.WatchSessionStarted += () => attached = true;

        await orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => _processMonitor.GameStateReads > 0);
        // An attach would be under way by now; there is nothing else to wait on for one not coming.
        await Task.Delay(100);

        Assert.False(attached);
        Assert.Single(_sessionStore.Saved);
    }

    [Fact]
    public async Task WatchMode_ASessionEndsWithTheGameStillUp_AttachesToIt()
    {
        // The other way round: the sweep sees the game on screen, and presence, which looks half
        // as often, has not reported it yet.
        _processMonitor.GameState = GameRuntimeState.Running;
        var orchestrator = CreateOrchestrator();
        await using var watch = await StartWatchModeAsync(
            orchestrator, new GamePresenceService(_processMonitor, NullLogger<GamePresenceService>.Instance));
        LaunchResult? watched = null;
        watch.WatchSessionEnded += result => watched = result;

        await orchestrator.RunSessionAsync(CompetitiveProfile);
        await WaitForAsync(() => watched is not null);

        Assert.True(watched!.Success);
        Assert.Equal(LaunchKind.Watch, watched.Session?.LaunchKind);
        Assert.Equal(2, _sessionStore.Saved.Count);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the session");
            }
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
