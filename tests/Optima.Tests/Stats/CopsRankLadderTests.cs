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
}
