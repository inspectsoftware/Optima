using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

public sealed class SessionStatsEnricherTests
{
    private static CopsProfileDelta Delta(CopsModeStats ranked, CopsModeStats? casual = null)
        => new(12, ranked, casual ?? CopsModeStats.Zero, CopsModeStats.Zero);

    private static readonly DateTimeOffset Started = new(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleRankedWinBecomesOneAutoMatch()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(18, 11, 3, 1, 0)), Started, sessionId: 7);

        var match = Assert.Single(matches);
        Assert.Equal("ranked", match.Mode);
        Assert.Equal("win", match.Result);
        Assert.Equal(18, match.Kills);
        Assert.Equal(11, match.Deaths);
        Assert.Equal(3, match.Assists);
        Assert.Equal(7, match.SessionId);
        Assert.Equal("auto", match.Source);
    }

    [Fact]
    public void SingleLossIsALoss()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(9, 14, 2, 0, 1)), Started, null);

        Assert.Equal("loss", Assert.Single(matches).Result);
    }

    [Fact]
    public void MultiMatchSessionsProduceNoAutoRows()
    {
        // Three ranked matches in one sitting: k/d cannot be attributed per match.
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(50, 40, 9, 2, 1)), Started, 3);

        Assert.Empty(matches);
    }

    [Fact]
    public void RankedAndCasualSinglesBothExtract()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(18, 11, 3, 1, 0), new CopsModeStats(12, 8, 4, 0, 1)),
            Started, 5);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Mode == "ranked" && m.Result == "win");
        Assert.Contains(matches, m => m.Mode == "casual" && m.Result == "loss");
    }

    [Fact]
    public void ZeroDeltaExtractsNothing()
        => Assert.Empty(SessionStatsEnricher.ExtractAutoMatches(
            Delta(CopsModeStats.Zero), Started, null));
}
