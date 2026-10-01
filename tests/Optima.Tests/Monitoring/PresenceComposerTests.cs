using Optima.Core.Monitoring;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class PresenceComposerTests
{
    private static readonly PlayerSeasonBadge Badge = new("frosty", 41, 23);

    [Fact]
    public void InGameWithFpsAndBadgeShowsBoth()
    {
        var text = PresenceComposer.Compose(GamePresence.InGame, 141.4, Badge, "Competitive");

        Assert.Equal("Critical Ops · 41W-23L", text.Details);
        Assert.Equal("frosty · 141 fps", text.State);
        Assert.Equal("Critical Ops session", text.LargeImageText);
    }

    [Fact]
    public void InGameWithoutBadgeFallsBackToFps()
    {
        var text = PresenceComposer.Compose(GamePresence.InGame, 59.6, null, null);

        Assert.Equal("Critical Ops", text.Details);
        Assert.Equal("60 fps", text.State);
    }

    [Fact]
    public void InGameWithoutFpsOrBadgeKeepsItClean()
    {
        var text = PresenceComposer.Compose(GamePresence.InGame, null, null, "Balanced");

        Assert.Equal("Critical Ops", text.Details);
        Assert.Equal("", text.State);
    }

    [Fact]
    public void ZeroWinsPlayerShowsTheRecord()
    {
        var text = PresenceComposer.Compose(GamePresence.InGame, null, new PlayerSeasonBadge("rookie", 0, 4), null);

        Assert.Equal("Critical Ops · 0W-4L", text.Details);
        Assert.Equal("rookie", text.State);
    }

    [Fact]
    public void LaunchingShowsTheProfileBeingApplied()
    {
        var text = PresenceComposer.Compose(GamePresence.Starting, null, Badge, "Competitive");

        Assert.Equal("Launching Critical Ops", text.Details);
        Assert.Equal("applying Competitive", text.State);
        Assert.Equal("Optima", text.LargeImageText);
    }

    [Fact]
    public void LaunchingWithoutAProfileOmitsTheState()
    {
        var text = PresenceComposer.Compose(GamePresence.Starting, null, Badge, " ");

        Assert.Equal("Launching Critical Ops", text.Details);
        Assert.Equal("", text.State);
    }

    [Fact]
    public void IdleIsTheLauncherCard()
    {
        var text = PresenceComposer.Compose(GamePresence.NotRunning, null, Badge, null);

        Assert.Equal("Optima Launcher", text.Details);
        Assert.Equal("Browsing the launcher", text.State);
        Assert.Equal("Optima", text.LargeImageText);
    }

    [Fact]
    public void OverlongStringsAreTruncatedWithAnEllipsis()
    {
        var longName = new string('x', 200);
        var text = PresenceComposer.Compose(GamePresence.InGame, 100, new PlayerSeasonBadge(longName, 1, 1), null);

        Assert.True(text.State.Length <= 120);
        Assert.EndsWith("…", text.State);
    }
}
