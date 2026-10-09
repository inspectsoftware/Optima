using Optima.Core.Models;
using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class BoostDialTimelineTests
{
    private static bool IsWholeThirdTurn(double degrees) => Math.Abs(degrees % 120) < 1e-6;

    [Fact]
    public void TheEndOfSwitchingOn_LooksExactlyLikeOn()
    {
        var end = BoostDialTimeline.At(BoostDialMode.Enabling, BoostDialTimeline.EnableMs);
        var on = BoostDialTimeline.At(BoostDialMode.On, 0);

        Assert.Equal(0, end.Detach, 6);
        // Three segments per set: any multiple of 120 degrees is the resting position again.
        Assert.True(IsWholeThirdTurn(end.OuterRotation));
        Assert.True(IsWholeThirdTurn(end.InnerRotation));
        Assert.Equal(on.Gradient, end.Gradient, 6);
        Assert.Equal(on.Progress, end.Progress, 6);
        Assert.Equal(0, end.PulseOpacity, 6);
        Assert.Equal(BoostDialLabel.Enabled, end.Label);
        Assert.Equal(1, end.LabelOpacity, 6);
    }

    [Fact]
    public void TheEndOfSwitchingOff_LooksExactlyLikeOff()
    {
        var end = BoostDialTimeline.At(BoostDialMode.Disabling, BoostDialTimeline.DisableMs);

        Assert.Equal(0, end.Detach, 6);
        Assert.True(IsWholeThirdTurn(end.OuterRotation));
        Assert.True(IsWholeThirdTurn(end.InnerRotation));
        Assert.Equal(0, end.Gradient, 6);
        Assert.Equal(BoostDialLabel.Idle, end.Label);
        Assert.Equal(1, end.LabelOpacity, 6);
    }

    [Fact]
    public void MidRun_TheSegmentsAreOut_Turning_AndCounting()
    {
        var mid = BoostDialTimeline.At(BoostDialMode.Enabling, 1300);

        Assert.Equal(1, mid.Detach, 3);
        Assert.True(mid.OuterRotation > 0);
        Assert.True(mid.InnerRotation < 0);
        Assert.InRange(mid.Progress, 0.4, 0.6);
        Assert.Equal(BoostDialLabel.Arming, mid.Label);
    }

    [Fact]
    public void TheSegmentsSeatOnTheSharedCurve_WithoutGoingPastHome()
    {
        var seating = Enumerable.Range(0, 41).Select(i => BoostDialTimeline.At(BoostDialMode.Enabling, 2000 + i * 10).Detach).ToList();

        // Fast away and a soft landing, like everything else that moves: never through the ring.
        Assert.All(seating, detach => Assert.InRange(detach, 0, 1));
        Assert.True(seating[10] < 0.5);
        Assert.Equal(0, seating[^1], 6);
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
        };

        var off = ticked.BoostEffective();
        Assert.False(off.BoostPriorityGuardEnabled || off.BoostStandbyCleanerEnabled || off.BoostTimerResolutionEnabled
            || off.BoostDemoteBackgroundEnabled || off.BoostKeepCoresAwake || off.BoostGpuHighPerformance);
        Assert.True(ticked.BoostStandbyCleanerEnabled);

        var on = (ticked with { BoostEnabled = true }).BoostEffective();
        Assert.True(on.BoostPriorityGuardEnabled && on.BoostStandbyCleanerEnabled && on.BoostTimerResolutionEnabled
            && on.BoostDemoteBackgroundEnabled && on.BoostKeepCoresAwake && on.BoostGpuHighPerformance);
    }
}
