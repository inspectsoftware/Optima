using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class MotionPolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public void Follows_windows_only_when_asked(bool windowsAnimationsOn, bool followWindows, bool expected)
    {
        Assert.Equal(expected, MotionPolicy.IsEnabled(windowsAnimationsOn, followWindows));
    }

    [Fact]
    public void Duration_is_zero_when_motion_is_off()
    {
        var designed = TimeSpan.FromMilliseconds(220);
        Assert.Equal(designed, MotionPolicy.Duration(designed, enabled: true));
        Assert.Equal(TimeSpan.Zero, MotionPolicy.Duration(designed, enabled: false));
    }
}
