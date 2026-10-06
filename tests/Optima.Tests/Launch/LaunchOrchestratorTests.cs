using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
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

    private LaunchOrchestrator CreateOrchestrator(SettingsService? settings = null) => new(
        _detector, [_launcher], _virtualDisplay, _displayService, _power,
        _processMonitor, _processOptimizer, _cleanup, CreateRecovery(), _metrics, _network, _sessionStore, _tweaks,
        NullLogger<LaunchOrchestrator>.Instance, settings);

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

        // Virtual display enabled, mode set, then original state restored.
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Contains("mode:1920x1080 @ 240 Hz", _virtualDisplay.Log);
        Assert.Contains("restoreOriginal", _virtualDisplay.Log);

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
        Assert.Contains("restoreTopology", _displayService.Log);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
    }

    [Fact]
    public async Task RunSession_GameNeverStarts_TimesOutAndRestores()
    {
        _processMonitor.GameStartPid = null;

        var result = await CreateOrchestrator().RunSessionAsync(CompetitiveProfile);

        Assert.False(result.Success);
        Assert.Equal("GAME_START_TIMEOUT", result.Error?.Code);
        Assert.Contains("restore", _power.Log);
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

        // The full profile went on and came off again, without any launch.
        Assert.Contains("apply:HighPerformance", _power.Log);
        Assert.Contains("restore", _power.Log);
        Assert.Contains("enable", _virtualDisplay.Log);
        Assert.Contains("restoreOriginal", _virtualDisplay.Log);
        Assert.Equal([4242], _processOptimizer.Applied);
        Assert.Equal([4242], _processOptimizer.Restored);
        Assert.True(_metrics.Started);
        Assert.Single(_sessionStore.Saved);
        Assert.False(File.Exists(_paths.PendingSnapshotFile));
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the priority keeper");
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
