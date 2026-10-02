using Optima.Core.Launch;
using Optima.Core.Monitoring;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class GameWatchPolicyTests
{
    private static PresenceChange Edge(GamePresence previous, GamePresence current)
        => new(previous, current, DateTimeOffset.UnixEpoch);

    [Fact]
    public void AttachesOnTheInGameEdge()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnPresenceChange(true, false, Edge(GamePresence.NotRunning, GamePresence.Starting)));
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));
    }

    [Fact]
    public void NeverAttachesWhenDisabled()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnPresenceChange(false, false, Edge(GamePresence.Starting, GamePresence.InGame)));
        Assert.Equal(WatchAction.None, policy.OnSessionEnded(false, false, gameRunning: true));
    }

    [Fact]
    public void NeverAttachesWhileASessionIsActive()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnPresenceChange(true, true, Edge(GamePresence.Starting, GamePresence.InGame)));
        Assert.Equal(WatchAction.None, policy.OnSessionEnded(true, true, gameRunning: true));
    }

    [Fact]
    public void AttachesWhenASessionEndsWhileTheGameStillRuns()
    {
        // The run was cancelled from the launcher but the game stayed up.
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnPresenceChange(true, true, Edge(GamePresence.Starting, GamePresence.InGame)));
        Assert.Equal(WatchAction.Attach, policy.OnSessionEnded(true, false, gameRunning: true));
    }

    [Fact]
    public void SessionEndedWithNoGameRunningAttachesNothing()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.None, policy.OnSessionEnded(true, false, gameRunning: false));
    }

    [Fact]
    public void NeverAttachesTwiceForTheSameGameRun()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));

        // A session that ends for the same run must not hand out a second attach.
        Assert.Equal(WatchAction.None, policy.OnSessionEnded(true, false, gameRunning: true));
    }

    [Fact]
    public void AttachesAgainForANewGameRun()
    {
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));

        // Game exits, then a new run starts.
        Assert.Equal(WatchAction.None, policy.OnPresenceChange(true, false, Edge(GamePresence.InGame, GamePresence.NotRunning)));
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));
    }

    [Fact]
    public void FailedAttachDoesNotRetryUntilTheGameRestarts()
    {
        // Attach was handed out; whatever happened to it, the same game run is not retried.
        var policy = new GameWatchPolicy();
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));
        Assert.Equal(WatchAction.None, policy.OnSessionEnded(true, false, gameRunning: true));

        // Only a fresh run re-arms it.
        policy.OnPresenceChange(true, false, Edge(GamePresence.InGame, GamePresence.NotRunning));
        Assert.Equal(WatchAction.Attach, policy.OnPresenceChange(true, false, Edge(GamePresence.Starting, GamePresence.InGame)));
    }
}
