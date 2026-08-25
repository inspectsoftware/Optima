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
    private readonly FakeSessionStore _sessionStore = new();

    public LaunchOrchestratorTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private RecoveryService CreateRecovery() => new(
        _paths, _store, _displayService, _power, _virtualDisplay, _processOptimizer,
        NullLogger<RecoveryService>.Instance);

    private LaunchOrchestrator CreateOrchestrator() => new(
        _detector, [_launcher], _virtualDisplay, _displayService, _power,
        _processMonitor, _processOptimizer, _cleanup, CreateRecovery(), _metrics, _sessionStore,
        NullLogger<LaunchOrchestrator>.Instance);

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
