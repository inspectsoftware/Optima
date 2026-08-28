using Optima.Core.Models;
using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class SessionTrendBuilderTests
{
    private static SessionRecord Session(long id, string hash = "h1", string[]? tweaks = null, int samples = 100) => new()
    {
        Id = id,
        ProfileName = "P",
        GamePackageId = "com.example",
        Stats = new SessionStats { AverageFps = 200, SampleCount = samples },
        ProfileHash = hash,
        TweakIds = tweaks ?? [],
    };

    [Fact]
    public void OrdersOldestFirstAndDropsSessionsWithoutData()
    {
        // Newest-first input: ids 3, 2 (no data), 1.
        var points = SessionTrendBuilder.Build([Session(3), Session(2, samples: 0), Session(1)]);
        Assert.Equal([1L, 3L], points.Select(p => p.Session.Id).ToArray());
    }

    [Fact]
    public void FlagsProfileHashChange()
    {
        var points = SessionTrendBuilder.Build([Session(2, hash: "h2"), Session(1, hash: "h1")]);
        Assert.False(points[0].ConfigChanged);
        Assert.True(points[1].ConfigChanged);
    }

    [Fact]
    public void FlagsTweakSetChange()
    {
        var points = SessionTrendBuilder.Build([Session(2, tweaks: ["game-dvr-off"]), Session(1)]);
        Assert.True(points[1].ConfigChanged);
    }

    [Fact]
    public void UnchangedConfigIsNotFlagged()
    {
        var points = SessionTrendBuilder.Build([Session(2, tweaks: ["a"]), Session(1, tweaks: ["a"])]);
        Assert.False(points[1].ConfigChanged);
    }

    [Fact]
    public void TakeLimitsToNewestSessions()
    {
        var newestFirst = Enumerable.Range(1, 30).Reverse().Select(i => Session(i)).ToList();
        var points = SessionTrendBuilder.Build(newestFirst, take: 20);
        Assert.Equal(20, points.Count);
        Assert.Equal(11, points[0].Session.Id);
        Assert.Equal(30, points[^1].Session.Id);
    }
}
