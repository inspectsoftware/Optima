using Optima.Core.Models;
using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class MotionPolicyTests
{
    [Theory]
    [InlineData(true, "System", true)]
    [InlineData(false, "System", false)]
    [InlineData(true, "On", true)]
    [InlineData(false, "On", true)]
    [InlineData(true, "Off", false)]
    [InlineData(false, "Off", false)]
    // Anything unreadable is the default, which is to do what Windows says.
    [InlineData(false, "nonsense", false)]
    [InlineData(true, null, true)]
    public void TheChoiceOfThreeDecides(bool windowsAnimationsOn, string? mode, bool expected)
    {
        Assert.Equal(expected, MotionPolicy.IsEnabled(windowsAnimationsOn, mode));
    }

    [Fact]
    public void Duration_is_zero_when_motion_is_off()
    {
        var designed = TimeSpan.FromMilliseconds(220);
        Assert.Equal(designed, MotionPolicy.Duration(designed, enabled: true));
        Assert.Equal(TimeSpan.Zero, MotionPolicy.Duration(designed, enabled: false));
    }

    [Fact]
    public void AConfigurationFromBeforeTheChoiceKeepsWhatItsCheckboxMeant()
    {
        Assert.Equal("System", new AppSettings().EffectiveAnimations);
        Assert.Equal("System", new AppSettings { FollowWindowsMotion = true }.EffectiveAnimations);
        // Unticked meant "move even when Windows says not to".
        Assert.Equal("On", new AppSettings { FollowWindowsMotion = false }.EffectiveAnimations);
        Assert.Equal("Off", new AppSettings { FollowWindowsMotion = false, Animations = "Off" }.EffectiveAnimations);
    }

    [Fact]
    public void TheCurveStartsFastAndLandsSoft()
    {
        Assert.Equal(0, MotionSpec.EaseOut(0));
        Assert.Equal(1, MotionSpec.EaseOut(1));
        // Most of the way there in the first half, and never past the end.
        Assert.True(MotionSpec.EaseOut(0.5) > 0.8);
        Assert.Equal(1, MotionSpec.EaseOut(7));
        Assert.Equal(0, MotionSpec.EaseOut(-3));
    }

    [Fact]
    public void AnEasedValueStartsWhereItIsFirstSent()
    {
        var value = new Eased();

        Assert.Equal(40, value.Get(40, 0.22, now: 0));
        Assert.False(value.Moving(0));
    }

    [Fact]
    public void AnEasedValueTravelsAndArrives()
    {
        var value = new Eased();
        value.Get(0, 0.4, now: 0);

        Assert.Equal(0, value.Get(100, 0.4, now: 1));
        Assert.True(value.Moving(1.1));
        var mid = value.Get(100, 0.4, now: 1.2);
        Assert.InRange(mid, 80, 99);
        Assert.Equal(100, value.Get(100, 0.4, now: 1.5), 6);
        Assert.False(value.Moving(1.5));
    }

    [Fact]
    public void SentSomewhereElseOnTheWay_ItSetsOffFromWhereItIs()
    {
        var value = new Eased();
        value.Get(0, 0.4, now: 0);
        value.Get(100, 0.4, now: 0);
        var reached = value.Get(100, 0.4, now: 0.2);

        // Redirected at 0.2 s: no jump at the moment of the turn, and it ends at the new target.
        Assert.Equal(reached, value.Get(20, 0.4, now: 0.2), 6);
        Assert.Equal(20, value.Get(20, 0.4, now: 0.6), 6);
    }

    [Fact]
    public void WithAnimationsOff_ItArrivesAtOnce()
    {
        var value = new Eased();
        value.Get(0, 0.4, now: 0);

        Assert.Equal(100, value.Get(100, seconds: 0, now: 0));
        Assert.False(value.Moving(0));
    }
}
