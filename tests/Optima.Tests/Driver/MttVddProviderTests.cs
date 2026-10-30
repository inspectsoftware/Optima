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

    [Theory]
    [InlineData(0, "None")]
    [InlineData(22, "Enable")]
    [InlineData(10, "Restart")]
    [InlineData(31, "Restart")]
    [InlineData(43, "Restart")]
    [InlineData(1, "Fail")]
    [InlineData(14, "Fail")]
    [InlineData(28, "Fail")]
    [InlineData(52, "Fail")]
    [InlineData(19, "Fail")]
    public void ActionFor_EachProblemCode(int code, string expected)
        => Assert.Equal(expected, MttVddProvider.ActionFor(code).ToString());

    [Theory]
    [InlineData(1, "VDD_DEVICE_NO_DRIVER")]
    [InlineData(18, "VDD_DEVICE_NO_DRIVER")]
    [InlineData(28, "VDD_DEVICE_NO_DRIVER")]
    [InlineData(14, "VDD_RESTART_NEEDED")]
    [InlineData(52, "VDD_DRIVER_BLOCKED")]
    [InlineData(43, "VDD_DEVICE_FAILED")]
    [InlineData(19, "VDD_DEVICE_FAILED")]
    public void ProblemError_NamesTheFault(int code, string expected)
    {
        var error = MttVddProvider.ProblemError(code).Error;

        Assert.Equal(expected, error.Code);
        Assert.NotNull(ErrorCatalog.Find(expected));
        Assert.Contains(code.ToString(), error.Title + " " + error.Explanation);
    }

    [Fact]
    public void ClosestAdvertisedMode_ExactMatch_ReturnsIt()
    {
        var closest = MttVddProvider.ClosestAdvertisedMode(Advertised, new DisplayMode(1920, 1080, 144));
        Assert.Equal(new DisplayMode(1920, 1080, 144), closest);
    }

    [Theory]
    [InlineData("Virtual Display Driver", true, true)]
    [InlineData("Virtual Display Driver", false, false)]
    [InlineData("NVIDIA GeForce RTX 4070", true, false)]
    public void IsMainScreen_OnlyThisDriversPrimaryDisplay(string adapter, bool primary, bool expected)
    {
        DisplayInfo[] displays =
        [
            new() { DeviceName = @"\.\DISPLAY1", FriendlyName = "Own", AdapterName = "Intel UHD", IsPrimary = !primary },
            new() { DeviceName = @"\.\DISPLAY2", FriendlyName = "Other", AdapterName = adapter, IsPrimary = primary },
        ];

        Assert.Equal(expected, MttVddProvider.IsMainScreen(displays));
    }
}
