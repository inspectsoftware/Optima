using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class GameWatchPolicyTests
{
    [Fact]
    public void AttachesAfterTwoConsecutiveRunningPolls()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnPoll(true, false, GameRuntimeState.Running));
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
    }

    [Fact]
    public void NeverAttachesWhenDisabled()
    {
        var policy = new GameWatchPolicy();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(WatchAction.None, policy.OnPoll(false, false, GameRuntimeState.Running));
        }
    }

    [Fact]
    public void NeverAttachesWhileASessionIsActive()
    {
        var policy = new GameWatchPolicy();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(WatchAction.None, policy.OnPoll(true, true, GameRuntimeState.Running));
        }
    }

    [Fact]
    public void AttachesOnceThePlaySessionEndsIfGameStillRuns()
    {
        // e.g. the PLAY session was cancelled but the game stayed up.
        var policy = new GameWatchPolicy();
        policy.OnPoll(true, true, GameRuntimeState.Running);
        policy.OnPoll(true, true, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
    }

    [Theory]
    [InlineData(GameRuntimeState.NotRunning)]
    [InlineData(GameRuntimeState.Starting)]
    [InlineData(GameRuntimeState.Exited)]
    public void NonRunningStatesNeverAttach(GameRuntimeState state)
    {
        var policy = new GameWatchPolicy();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(WatchAction.None, policy.OnPoll(true, false, state));
        }
    }

    [Fact]
    public void DebounceResetsWhenRunningIsInterrupted()
    {
        var policy = new GameWatchPolicy();
        policy.OnPoll(true, false, GameRuntimeState.Running);
        policy.OnPoll(true, false, GameRuntimeState.Starting);
        Assert.Equal(WatchAction.None, policy.OnPoll(true, false, GameRuntimeState.Running));
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
    }

    [Fact]
    public void NeverAttachesTwiceForTheSameGameRun()
    {
        var policy = new GameWatchPolicy();
        policy.OnPoll(true, false, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(WatchAction.None, policy.OnPoll(true, false, GameRuntimeState.Running));
        }
    }

    [Fact]
    public void AttachesAgainForANewGameRun()
    {
        var policy = new GameWatchPolicy();
        policy.OnPoll(true, false, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));

        // Game exits, then a new run starts.
        policy.OnPoll(true, false, GameRuntimeState.NotRunning);
        policy.OnPoll(true, false, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
    }

    [Fact]
    public void FailedAttachDoesNotRetryUntilTheGameRestarts()
    {
        // Attach was handed out; whatever happened to it, the same game run is not retried.
        var policy = new GameWatchPolicy();
        policy.OnPoll(true, false, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
        Assert.Equal(WatchAction.None, policy.OnPoll(true, false, GameRuntimeState.Running));

        policy.OnPoll(true, false, GameRuntimeState.Exited);
        policy.OnPoll(true, false, GameRuntimeState.Running);
        Assert.Equal(WatchAction.Attach, policy.OnPoll(true, false, GameRuntimeState.Running));
    }
}
