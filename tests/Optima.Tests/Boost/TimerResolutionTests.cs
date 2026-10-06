using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Boost;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Platform.Windows.Services;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class TimerResolutionTests
{
    [Theory]
    [InlineData(18363, TimerResolutionReach.SystemWide)]                    // Windows 10 1909
    [InlineData(19041, TimerResolutionReach.OwnProcessOnly)]                // Windows 10 2004
    [InlineData(19045, TimerResolutionReach.OwnProcessOnly)]                // Windows 10 22H2
    [InlineData(22000, TimerResolutionReach.OwnProcessUnlessGlobalSwitch)]  // Windows 11 21H2
    [InlineData(26100, TimerResolutionReach.OwnProcessUnlessGlobalSwitch)]  // Windows 11 24H2
    public void TheReachOfARequestFollowsTheWindowsBuild(int build, TimerResolutionReach expected)
        => Assert.Equal(expected, TimerResolutionPolicy.ReachFor(build));

    [Theory]
    [InlineData(18363, false, true)]
    [InlineData(19045, false, false)]
    [InlineData(19045, true, false)]   // Windows 10 2004+ has no switch that changes it
    [InlineData(22631, false, false)]
    [InlineData(22631, true, true)]
    public void OptimasHoldReachesTheGameOnlyWhereWindowsAllowsIt(int build, bool globalSwitch, bool expected)
        => Assert.Equal(expected, TimerResolutionPolicy.HoldReachesGame(build, globalSwitch));

    [Fact]
    public void TheChoiceBecomesWindowsUnits()
    {
        Assert.Equal(10_000u, TimerResolutionPolicy.ToHundredNanoseconds("1.0"));
        Assert.Equal(5_000u, TimerResolutionPolicy.ToHundredNanoseconds("0.5"));
        Assert.Equal(10_000u, TimerResolutionPolicy.ToHundredNanoseconds(null));
    }

    private sealed class FakeTimer : ITimerResolution
    {
        public int WindowsBuild { get; set; } = 22631;
        public bool GlobalRequestsEnabled { get; set; }
        public double CurrentMs => Held is { } held ? held / 10_000.0 : 15.625;
        public uint? Held { get; private set; }
        public int Releases { get; private set; }
        public List<int> Honored { get; } = [];

        public double? Hold(uint hundredNanoseconds)
        {
            Held = hundredNanoseconds;
            return hundredNanoseconds / 10_000.0;
        }

        public void Release()
        {
            Held = null;
            Releases++;
        }

        public void HonorRequestsOf(int processId) => Honored.Add(processId);
    }

    private readonly FakeTimer _timer = new();
    private readonly FakeProcessMonitor _monitor = new();
    private readonly GamePresenceService _presence;
    private AppSettings _settings = new() { BoostTimerResolutionEnabled = true, BoostTimerResolution = "0.5" };

    public TimerResolutionTests()
    {
        _presence = new GamePresenceService(_monitor, NullLogger<GamePresenceService>.Instance);
        _monitor.Tracked = [new TrackedProcess { ProcessId = 77, Name = "crosvm", Kind = TrackedProcessKind.Emulator }];
    }

    private TimerResolutionService Create()
        => new(_timer, _monitor, _presence, () => _settings, NullLogger<TimerResolutionService>.Instance);

    [Fact]
    public async Task HeldOnlyWhileTheGameIsOnScreen()
    {
        using var service = Create();

        await service.SyncAsync();
        Assert.Null(_timer.Held);

        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        await service.SyncAsync();
        Assert.Equal(5_000u, _timer.Held);
        Assert.Equal([77], _timer.Honored);
        Assert.True(service.Status.Holding);
        Assert.Equal(0.5, service.Status.AppliedMs);
    }

    [Fact]
    public async Task SwitchedOffMidGame_LetsGo()
    {
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        using var service = Create();
        await service.SyncAsync();

        _settings = _settings with { BoostTimerResolutionEnabled = false };
        await service.SyncAsync();

        Assert.Null(_timer.Held);
        Assert.Equal(1, _timer.Releases);
        Assert.False(service.Status.Holding);
    }

    [Fact]
    public async Task Dispose_ReleasesTheHold()
    {
        _presence.ApplyState(GameRuntimeState.Running, DateTimeOffset.UtcNow);
        var service = Create();
        await service.SyncAsync();

        service.Dispose();

        Assert.Null(_timer.Held);
    }

    [Fact]
    public void WindowsAppliesAndReleasesARealRequest()
    {
        var timer = new WindowsTimerResolution(NullLogger<WindowsTimerResolution>.Instance);

        var applied = timer.Hold(10_000);
        try
        {
            Assert.NotNull(applied);
            // Another program may already hold something finer; it is never coarser than what was asked.
            Assert.InRange(timer.CurrentMs, 0.4, 1.05);
        }
        finally
        {
            timer.Release();
        }
        Assert.True(timer.WindowsBuild > 10000);
    }
}
