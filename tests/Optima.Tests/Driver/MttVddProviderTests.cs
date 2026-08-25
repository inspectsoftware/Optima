using Optima.Core.Models;
using Optima.Driver.Providers;
using Xunit;

namespace Optima.Tests.Driver;

public class MttVddProviderTests
{
    private static readonly IReadOnlyList<DisplayMode> Advertised =
    [
        new(1920, 1080, 60), new(1920, 1080, 144), new(1920, 1080, 244),
        new(2560, 1440, 144), new(2560, 1440, 244),
    ];

    [Fact]
    public void ClosestAdvertisedMode_SameResolution_PicksNearestRefresh()
    {
        var closest = MttVddProvider.ClosestAdvertisedMode(Advertised, new DisplayMode(1920, 1080, 240));
        Assert.Equal(new DisplayMode(1920, 1080, 244), closest);
    }

    [Fact]
    public void ClosestAdvertisedMode_UnknownResolution_PicksNearestArea()
    {
        var closest = MttVddProvider.ClosestAdvertisedMode(Advertised, new DisplayMode(2560, 1600, 165));
        Assert.Equal(new DisplayMode(2560, 1440, 144), closest);
    }

    [Fact]
    public void ClosestAdvertisedMode_EmptyList_ReturnsNull()
    {
        Assert.Null(MttVddProvider.ClosestAdvertisedMode([], new DisplayMode(1920, 1080, 240)));
    }

    [Fact]
    public void ClosestAdvertisedMode_ExactMatch_ReturnsIt()
    {
        var closest = MttVddProvider.ClosestAdvertisedMode(Advertised, new DisplayMode(1920, 1080, 144));
        Assert.Equal(new DisplayMode(1920, 1080, 144), closest);
    }
}
