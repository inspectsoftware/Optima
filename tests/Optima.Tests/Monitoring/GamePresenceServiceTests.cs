using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class GamePresenceServiceTests
{
    private sealed class FakeProcessMonitor : IProcessMonitor
    {
        public Task<IReadOnlyList<TrackedProcess>> GetTrackedProcessesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TrackedProcess>>([]);

        public Task<GameRuntimeState> GetGameStateAsync(CancellationToken ct = default)
            => Task.FromResult(GameRuntimeState.NotRunning);

        public Task<int?> WaitForGameStartAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<int?>(null);

        public Task WaitForGameExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static GamePresenceService Create() =>
        new(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);

    private static readonly DateTimeOffset T0 = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void StartupToInGameRaisesOrderedChanges()
    {
        var svc = Create();
        var changes = new List<PresenceChange>();
        svc.PresenceChanged += changes.Add;

        svc.ApplyState(GameRuntimeState.NotRunning, At(0));
        svc.ApplyState(GameRuntimeState.Starting, At(2));
        svc.ApplyState(GameRuntimeState.Running, At(4));

        Assert.Equal(
            [(GamePresence.NotRunning, GamePresence.Starting), (GamePresence.Starting, GamePresence.InGame)],
            changes.Select(c => (c.Previous, c.Current)).ToArray());
        Assert.Equal(GamePresence.InGame, svc.Current);
        Assert.Equal(At(4), svc.InGameSince);
    }

    [Fact]
    public void DirectFallToNotRunningExitsImmediately()
    {
        var svc = Create();
        var exits = new List<GameExit>();
        svc.GameExited += exits.Add;

        svc.ApplyState(GameRuntimeState.Running, At(0));
        svc.ApplyState(GameRuntimeState.NotRunning, At(120));

        var exit = Assert.Single(exits);
        Assert.False(exit.EmulatorStillAlive);
        Assert.Equal(TimeSpan.FromSeconds(120), exit.RunDuration);
        Assert.Equal(GamePresence.NotRunning, svc.Current);
    }

    [Fact]
    public void FallToStartingIsDebouncedThenExitsWithEmulatorAliveHint()
    {
        var svc = Create();
        var exits = new List<GameExit>();
        svc.GameExited += exits.Add;

        svc.ApplyState(GameRuntimeState.Running, At(0));
        svc.ApplyState(GameRuntimeState.Starting, At(2));
        svc.ApplyState(GameRuntimeState.Starting, At(4));
        Assert.Empty(exits);
        Assert.Equal(GamePresence.InGame, svc.Current);

        svc.ApplyState(GameRuntimeState.Starting, At(6));

        var exit = Assert.Single(exits);
        Assert.True(exit.EmulatorStillAlive);
        Assert.Equal(GamePresence.NotRunning, svc.Current);
    }

    [Fact]
    public void WindowFlickerDoesNotEndTheRun()
    {
        var svc = Create();
        var exits = new List<GameExit>();
        svc.GameExited += exits.Add;

        svc.ApplyState(GameRuntimeState.Running, At(0));
        svc.ApplyState(GameRuntimeState.Starting, At(2));
        svc.ApplyState(GameRuntimeState.Running, At(4));
        svc.ApplyState(GameRuntimeState.Starting, At(6));
        svc.ApplyState(GameRuntimeState.Running, At(8));

        Assert.Empty(exits);
        Assert.Equal(GamePresence.InGame, svc.Current);
        Assert.Equal(At(0), svc.InGameSince);
    }

    [Fact]
    public void ExitFiresExactlyOncePerRun()
    {
        var svc = Create();
        var exits = new List<GameExit>();
        svc.GameExited += exits.Add;

        svc.ApplyState(GameRuntimeState.Running, At(0));
        svc.ApplyState(GameRuntimeState.NotRunning, At(60));
        svc.ApplyState(GameRuntimeState.NotRunning, At(62));
        svc.ApplyState(GameRuntimeState.NotRunning, At(64));

        Assert.Single(exits);

        // A second run raises a second, separate exit.
        svc.ApplyState(GameRuntimeState.Running, At(100));
        svc.ApplyState(GameRuntimeState.NotRunning, At(160));
        Assert.Equal(2, exits.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), exits[1].RunDuration);
    }

    [Fact]
    public void TickedFiresEveryPollWithRawState()
    {
        var svc = Create();
        var ticks = new List<GameRuntimeState>();
        svc.Ticked += ticks.Add;

        svc.ApplyState(GameRuntimeState.NotRunning, At(0));
        svc.ApplyState(GameRuntimeState.Running, At(2));
        svc.ApplyState(GameRuntimeState.Running, At(4));

        Assert.Equal([GameRuntimeState.NotRunning, GameRuntimeState.Running, GameRuntimeState.Running], ticks);
    }
}
