using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

public sealed class CopsApiParsingTests
{
    [Fact]
    public void ParsesTheRealCapturedResponse()
    {
        var profile = CopsProfileParser.Parse(CopsApiFixtures.RealProfileResponse);

        Assert.NotNull(profile);
        Assert.Equal("frosty", profile!.Name);
        Assert.Equal(259425939, profile.UserId);
        Assert.Equal(1, profile.Level);
        Assert.True(profile.Seasons.Count >= 9, $"expected many seasons, got {profile.Seasons.Count}");
        Assert.NotNull(profile.CurrentSeason);
        Assert.Equal(profile.Seasons.Max(s => s.Season), profile.CurrentSeason!.Season);
    }

    [Fact]
    public void TheRealEndpointAnswersWithAnArrayRoot()
    {
        // usernames= takes a comma list, so even a single lookup comes back as [ {...} ].
        Assert.StartsWith("[", CopsApiFixtures.RealProfileResponse.TrimStart());
    }

    [Fact]
    public void SingleObjectRootAlsoParses()
    {
        var profile = CopsProfileParser.Parse("""{"basicInfo":{"userID":7,"name":"solo"}}""");
        Assert.NotNull(profile);
        Assert.Equal("solo", profile!.Name);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void GarbageAndEmptyShapesReturnNull(string json)
        => Assert.Null(CopsProfileParser.Parse(json));

    [Fact]
    public void MissingStatsSectionStillParsesBasics()
    {
        var profile = CopsProfileParser.Parse("""{"basicInfo":{"userID":42,"name":"tester"}}""");
        Assert.NotNull(profile);
        Assert.Equal(42, profile!.UserId);
        Assert.Equal("tester", profile.Name);
        Assert.Empty(profile.Seasons);
        Assert.Null(profile.CurrentSeason);
    }

    [Fact]
    public void MissingModeFieldsBecomeZeros()
    {
        var profile = CopsProfileParser.Parse(
            """{"basicInfo":{"name":"x"},"stats":{"seasonal_stats":[{"season":3,"ranked":{"k":7}}]}}""");
        var season = Assert.Single(profile!.Seasons);
        Assert.Equal(3, season.Season);
        Assert.Equal(7, season.Ranked.Kills);
        Assert.Equal(0, season.Ranked.Deaths);
        Assert.Equal(CopsModeStats.Zero, season.Casual);
        Assert.Equal(CopsModeStats.Zero, season.Custom);
    }
}

public sealed class CopsProfileDeltaTests
{
    private static CopsPlayerProfile ProfileWith(params CopsSeasonStats[] seasons)
        => new(1, "p", 10, seasons);

    private static CopsSeasonStats Season(int season, CopsModeStats ranked)
        => new(season, ranked, CopsModeStats.Zero, CopsModeStats.Zero);

    [Fact]
    public void SimpleSessionDeltaIsFieldWise()
    {
        var before = ProfileWith(Season(5, new CopsModeStats(100, 80, 30, 40, 35)));
        var after = ProfileWith(Season(5, new CopsModeStats(118, 91, 33, 41, 36)));

        var delta = CopsProfileDelta.Between(before, after);

        Assert.NotNull(delta);
        Assert.Equal(5, delta!.Season);
        Assert.Equal(new CopsModeStats(18, 11, 3, 1, 1), delta.Ranked);
        Assert.True(delta.Casual.IsZero);
    }

    [Fact]
    public void SeasonRolloverUsesTheNewSeasonRawValues()
    {
        var before = ProfileWith(Season(5, new CopsModeStats(500, 400, 100, 200, 180)));
        var after = ProfileWith(
            Season(5, new CopsModeStats(500, 400, 100, 200, 180)),
            Season(6, new CopsModeStats(12, 9, 2, 1, 0)));

        var delta = CopsProfileDelta.Between(before, after);

        Assert.Equal(6, delta!.Season);
        Assert.Equal(new CopsModeStats(12, 9, 2, 1, 0), delta.Ranked);
    }

    [Fact]
    public void ServerCorrectionsNeverGoNegative()
    {
        var before = ProfileWith(Season(5, new CopsModeStats(100, 100, 100, 50, 50)));
        var after = ProfileWith(Season(5, new CopsModeStats(90, 105, 100, 49, 51)));

        var delta = CopsProfileDelta.Between(before, after);

        Assert.Equal(new CopsModeStats(0, 5, 0, 0, 1), delta!.Ranked);
    }

    [Fact]
    public void MissingSnapshotsHandledGracefully()
    {
        var after = ProfileWith(Season(2, new CopsModeStats(5, 3, 1, 1, 0)));

        Assert.Null(CopsProfileDelta.Between(after, null));
        var noBaseline = CopsProfileDelta.Between(null, after);
        Assert.Equal(new CopsModeStats(5, 3, 1, 1, 0), noBaseline!.Ranked);
    }

    [Fact]
    public void MatchesCountedIsWinsPlusLosses()
        => Assert.Equal(3, new CopsModeStats(10, 5, 2, 2, 1).MatchesCounted);
}
