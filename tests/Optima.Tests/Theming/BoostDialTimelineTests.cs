using Optima.Core.Models;
using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class BoostDialTimelineTests
{
    // The gears turn with their brushed metal, so only a whole turn looks the same again; a small
    // gear turns -3 times the drive gear, so a whole turn of the drive gear is whole turns for all.
    private static bool IsWholeTurn(double degrees) => Math.Abs(Math.IEEERemainder(degrees, 360)) < 1e-6;

    private static void AssertSameLook(BoostDialFrame expected, BoostDialFrame actual)
    {
        Assert.True(IsWholeTurn(actual.Drive - expected.Drive), $"drive {actual.Drive} against {expected.Drive}");
        Assert.Equal(expected.Heat, actual.Heat, 6);
        Assert.Equal(expected.Lit, actual.Lit, 6);
        Assert.Equal(expected.Aura, actual.Aura, 6);
        Assert.Equal(expected.Sparks, actual.Sparks, 6);
        Assert.Equal(expected.Embers, actual.Embers, 6);
        Assert.Equal(0, actual.Flare, 6);
        Assert.Equal(0, actual.ShakeX, 6);
        Assert.Equal(0, actual.ShakeY, 6);
        Assert.Equal(expected.Label, actual.Label);
        Assert.Equal(1, actual.LabelOpacity, 6);
    }

    [Fact]
    public void TheEndOfIgnition_LooksExactlyLikeTheSteadyRunStarting()
    {
        const double end = BoostDialTimeline.EnableMs;
        var on = BoostDialTimeline.At(BoostDialMode.On, 0);

        AssertSameLook(on, BoostDialTimeline.At(BoostDialMode.Enabling, end));
        Assert.Equal(1080, BoostDialTimeline.At(BoostDialMode.Enabling, end).Drive, 6);
        // The sparks and embers carry on mid-flight instead of starting over.
        Assert.Equal(on.ParticleMs, BoostDialTimeline.At(BoostDialMode.Enabling, end).ParticleMs, 6);
        for (var k = 0; k < BoostDialTimeline.Plates; k++)
        {
            Assert.Equal(0, BoostDialTimeline.PlateOut(BoostDialMode.Enabling, end, k), 6);
        }
        for (var k = 0; k < BoostDialTimeline.Gears; k++)
        {
            Assert.Equal(1, BoostDialTimeline.GearHeat(BoostDialMode.Enabling, end, k), 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2_500)]
    [InlineData(11_999)]
    [InlineData(97_321)]
    public void TheEndOfCoolDown_LooksExactlyLikeRest_WhereverTheSteadyRunWas(double ranMs)
    {
        const double end = BoostDialTimeline.DisableMs;
        var off = BoostDialTimeline.At(BoostDialMode.Off, 0);

        AssertSameLook(off, BoostDialTimeline.At(BoostDialMode.Disabling, end, ranMs));
        for (var k = 0; k < BoostDialTimeline.Plates; k++)
        {
            Assert.Equal(1, BoostDialTimeline.PlateOut(BoostDialMode.Disabling, end, k), 6);
        }
        for (var k = 0; k < BoostDialTimeline.Gears; k++)
        {
            Assert.Equal(0, BoostDialTimeline.GearHeat(BoostDialMode.Disabling, end, k), 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4_100)]
    [InlineData(25_000)]
    public void TheCoolDown_StartsWhereTheSteadyRunWas(double ranMs)
    {
        var on = BoostDialTimeline.At(BoostDialMode.On, ranMs);
        var start = BoostDialTimeline.At(BoostDialMode.Disabling, 0, ranMs);

        Assert.Equal(on.Drive, start.Drive, 6);
        Assert.Equal(on.Heat, start.Heat, 6);
        Assert.Equal(on.Aura, start.Aura, 6);
        Assert.Equal(on.ParticleMs, start.ParticleMs, 6);
        // And it unwinds backwards, a turn and a bit.
        Assert.True(BoostDialTimeline.At(BoostDialMode.Disabling, 300, ranMs).Drive < start.Drive);
    }

    [Fact]
    public void Engaged_TheDriveGearTurnsOnceEveryTwelveSeconds_TheSmallOnesOnceEveryFour()
    {
        Assert.Equal(120, BoostDialTimeline.At(BoostDialMode.On, 4_000).Drive, 6);
        Assert.True(IsWholeTurn(BoostDialTimeline.At(BoostDialMode.On, 12_000).Drive));
        Assert.InRange(BoostDialTimeline.At(BoostDialMode.On, 30_000).Heat, 0.8, 1);
    }

    [Fact]
    public void IgnitionSpinsThreeTurns_SlowAtBothEnds_AndShakesUntilItLocks()
    {
        var early = BoostDialTimeline.At(BoostDialMode.Enabling, 200);
        var mid = BoostDialTimeline.At(BoostDialMode.Enabling, 1_200);

        Assert.InRange(early.Drive, 0, 20);
        Assert.Equal(540, mid.Drive, 6);
        Assert.Equal(BoostDialLabel.Igniting, mid.Label);
        Assert.Contains(Enumerable.Range(0, 20), i => Math.Abs(BoostDialTimeline.At(BoostDialMode.Enabling, 300 + i * 13).ShakeX) > 0.5);
    }

    [Fact]
    public void ThePlatesClampOneAfterAnother_AndSlamOnlyALittlePastTheRim()
    {
        var t = 200;
        Assert.True(BoostDialTimeline.PlateOut(BoostDialMode.Enabling, t, 0) < BoostDialTimeline.PlateOut(BoostDialMode.Enabling, t, 8));

        var travel = Enumerable.Range(0, 241).Select(i => BoostDialTimeline.PlateOut(BoostDialMode.Enabling, i * 10, 4)).ToList();
        Assert.All(travel, plateOut => Assert.InRange(plateOut, -0.15, 1));
        Assert.True(travel.Min() < -0.05);
    }

    [Fact]
    public void TheGearsHeatUpRoundTheRing_FromTheTop()
    {
        var t = 900;
        var heat = Enumerable.Range(0, BoostDialTimeline.Gears).Select(k => BoostDialTimeline.GearHeat(BoostDialMode.Enabling, t, k)).ToList();

        Assert.Equal(heat.OrderByDescending(h => h), heat);
        Assert.True(heat[0] > 0.9 && heat[5] == 0);
    }

    [Fact]
    public void TheMasterSwitchOff_SwitchesEveryFeatureOff_WithoutForgettingTheTicks()
    {
        var ticked = new AppSettings
        {
            BoostPriorityGuardEnabled = true,
            BoostStandbyCleanerEnabled = true,
            BoostTimerResolutionEnabled = true,
            BoostDemoteBackgroundEnabled = true,
            BoostKeepCoresAwake = true,
            BoostGpuHighPerformance = true,
            BoostPowerFloor = true,
        };

        var off = ticked.BoostEffective();
        Assert.False(off.BoostPriorityGuardEnabled || off.BoostStandbyCleanerEnabled || off.BoostTimerResolutionEnabled
            || off.BoostDemoteBackgroundEnabled || off.BoostKeepCoresAwake || off.BoostGpuHighPerformance || off.BoostPowerFloor);
        Assert.True(ticked.BoostStandbyCleanerEnabled);

        var on = (ticked with { BoostEnabled = true }).BoostEffective();
        Assert.True(on.BoostPriorityGuardEnabled && on.BoostStandbyCleanerEnabled && on.BoostTimerResolutionEnabled
            && on.BoostDemoteBackgroundEnabled && on.BoostKeepCoresAwake && on.BoostGpuHighPerformance && on.BoostPowerFloor);
    }
}
