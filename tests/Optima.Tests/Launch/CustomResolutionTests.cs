using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Launch;

/// <summary>The custom resolution window: what counts as a size, what it offers, and when Restore means anything.</summary>
public sealed class CustomResolutionTests
{
    private static readonly DisplayMode[] Modes =
    [
        new(1920, 1080, 60), new(1920, 1080, 144), new(1920, 1080, 144), new(1280, 720, 1), new(1280, 720, 60),
    ];

    [Theory]
    [InlineData("1920", "1440", true)]
    [InlineData(" 640 ", "480", true)]
    [InlineData("639", "480", false)]
    [InlineData("640", "479", false)]
    [InlineData("-1920", "1440", false)]
    [InlineData("abc", "1440", false)]
    [InlineData("", "", false)]
    [InlineData(null, "1440", false)]
    [InlineData("1920", null, false)]
    public void Sizes_are_read_within_the_limits(string? width, string? height, bool expected)
        => Assert.Equal(expected, CustomResolution.TryParse(width, height, out _, out _));

    [Fact]
    public void A_good_size_comes_back_as_typed()
    {
        Assert.True(CustomResolution.TryParse("1920", "1440", out var width, out var height));
        Assert.Equal((1920, 1440), (width, height));
    }

    [Fact]
    public void A_size_is_listed_only_with_both_sides_matching()
    {
        Assert.True(CustomResolution.IsListed(Modes, 1920, 1080));
        Assert.False(CustomResolution.IsListed(Modes, 1920, 1440));
    }

    [Fact]
    public void A_listed_size_offers_its_own_rates_highest_first()
        => Assert.Equal([144, 60], CustomResolution.RatesFor(Modes, 1920, 1080, 240));

    [Fact]
    public void The_hardware_default_rate_is_not_offered()
        => Assert.Equal([60], CustomResolution.RatesFor(Modes, 1280, 720, 240));

    [Fact]
    public void A_size_not_listed_offers_the_current_rate()
        => Assert.Equal([240], CustomResolution.RatesFor(Modes, 1920, 1440, 240));

    [Fact]
    public void Restore_needs_a_saved_mode_that_differs()
    {
        var current = new DisplayMode(1920, 1440, 144);
        Assert.True(CustomResolution.CanRestore(current, new DisplayMode(1920, 1080, 144)));
        Assert.False(CustomResolution.CanRestore(current, current));
        Assert.False(CustomResolution.CanRestore(current, null));
        Assert.False(CustomResolution.CanRestore(current, new DisplayMode(0, 0, 0)));
    }
}
