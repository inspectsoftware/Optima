using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

public sealed class CopsRankLadderTests
{
    [Fact]
    public void WendigoIsPlatinumPerMmr()
    {
        // Empirical pair from the live API: rank 5, mmr 1530 = Platinum.
        var rank = CopsRankLadder.Resolve(5, 1530);
        Assert.NotNull(rank);
        Assert.Equal("Platinum", rank!.Name);
    }

    [Fact]
    public void FrostyIsCalibrating()
    {
        // Empirical pair: rank 0 with 5 placement matches left, mmr 1270 = still calibrating.
        var rank = CopsRankLadder.Resolve(0, 1270);
        Assert.NotNull(rank);
        Assert.Equal("Calibrating", rank!.Name);
    }

    [Fact]
    public void MmrIsTheAuthorityWhenTheIndexDisagrees()
    {
        // 1410 is Gold by rating even if a stale index said Silver.
        var rank = CopsRankLadder.Resolve(3, 1410);
        Assert.Equal("Gold", rank!.Name);
    }

    [Fact]
    public void OfficialBoundariesHold()
    {
        // Rating decay can drop a placed player below their old index; the rating rules.
        Assert.Equal("Iron", CopsRankLadder.Resolve(2, 1199)!.Name);   // Iron ends at 1200
        Assert.Equal("Bronze", CopsRankLadder.Resolve(2, 1200)!.Name); // Bronze starts
        Assert.Equal("Silver", CopsRankLadder.Resolve(3, 1300)!.Name);
        Assert.Equal("Master", CopsRankLadder.Resolve(7, 1750)!.Name);
        Assert.Equal("Spec Ops", CopsRankLadder.Resolve(8, 1800)!.Name);
        Assert.Equal("Elite Ops", CopsRankLadder.Resolve(9, 1950)!.Name);
    }

    [Fact]
    public void DivisionsFollowThe25PointSteps()
    {
        Assert.Equal(1, CopsRankLadder.Division(5, 1500));
        Assert.Equal(2, CopsRankLadder.Division(5, 1525));
        Assert.Equal(4, CopsRankLadder.Division(5, 1575));
        Assert.Null(CopsRankLadder.Division(8, 1800));   // Spec Ops has no divisions
    }

    [Fact]
    public void MmrOnlyLookupsStillWork()
    {
        Assert.Equal("Gold", CopsRankLadder.Resolve(null, 1450)!.Name);
        Assert.Null(CopsRankLadder.Resolve(null, null));
    }

    [Theory]
    [InlineData(1570, 3)]   // a hard case for the step arithmetic: 70 past the band, not a multiple of 25
    [InlineData(1599, 4)]   // the top of the band a step short of promotion
    [InlineData(1600, 1)]   // and the bottom of the next one
    public void DivisionsKeepCountingToTheTopOfTheBand(long mmr, int division)
        => Assert.Equal(division, CopsRankLadder.Division(5, mmr));

    [Fact]
    public void APlayerStillCalibratingHasNoDivision()
    {
        // The provisional rating is not inside a tier, so it cannot have a division: reading one out of it
        // produced a confident "Calibrating 4" for a player who has not placed at all. Neither does a
        // rating above Master, and a profile with no rating has nowhere to stand.
        Assert.Null(CopsRankLadder.Division(0, 1180));
        Assert.Null(CopsRankLadder.Division(9, 2013));
        Assert.Null(CopsRankLadder.Division(null, null));
    }

    [Fact]
    public void TheNextTierIsTheOneAbove()
    {
        Assert.Equal("Diamond", CopsRankLadder.NextTier(5, 1570)!.Name);
        Assert.Equal("Master", CopsRankLadder.NextTier(6, 1650)!.Name);

        // Nothing is above Elite Ops, and a player who has not placed is not climbing towards Iron.
        Assert.Null(CopsRankLadder.NextTier(9, 2013));
        Assert.Null(CopsRankLadder.NextTier(0, 1180));
    }

    [Fact]
    public void ProgressIsTheRatingLeftInTheBand()
    {
        var (remaining, progress) = CopsRankLadder.ProgressToNextTier(5, 1570)!.Value;

        Assert.Equal(30, remaining);
        Assert.Equal(0.7, progress, 3);

        // The bottom of the band is empty progress; the top is full.
        Assert.Equal(100, CopsRankLadder.ProgressToNextTier(5, 1500)!.Value.Remaining);
        Assert.Equal(0.0, CopsRankLadder.ProgressToNextTier(5, 1500)!.Value.Progress, 3);
        Assert.Equal(1, CopsRankLadder.ProgressToNextTier(5, 1599)!.Value.Remaining);

        // Open ended or unplaced: there is no band to fill, so the card shows no bar rather than a made-up
        // one.
        Assert.Null(CopsRankLadder.ProgressToNextTier(9, 2013));
        Assert.Null(CopsRankLadder.ProgressToNextTier(0, 1180));
    }

    [Theory]
    [InlineData(5, 1570, "Platinum 3")]
    [InlineData(6, 1650, "Diamond 3")]
    [InlineData(0, 1180, "Calibrating")]
    [InlineData(9, 2013, "Elite Ops")]
    public void LabelSpellsTheRankTheWayTheAppShowsIt(int tier, long mmr, string expected)
        => Assert.Equal(expected, CopsRankLadder.Label(tier, mmr));
}
