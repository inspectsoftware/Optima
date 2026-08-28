using Optima.Core.Launch;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class OverlayPlacementTests
{
    private static readonly OverlayRect WorkArea = new(100, 50, 1920, 1040);

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 116, 66)]
    [InlineData(OverlayCorner.TopRight, 1804, 66)]
    [InlineData(OverlayCorner.BottomLeft, 116, 994)]
    [InlineData(OverlayCorner.BottomRight, 1804, 994)]
    public void PlacesInEachCorner(OverlayCorner corner, double expectedX, double expectedY)
    {
        var (x, y) = OverlayPlacement.Compute(corner, WorkArea, overlayWidth: 200, overlayHeight: 80);
        Assert.Equal(expectedX, x, 3);
        Assert.Equal(expectedY, y, 3);
    }

    [Theory]
    [InlineData("TopLeft", OverlayCorner.TopLeft)]
    [InlineData("bottomright", OverlayCorner.BottomRight)]
    [InlineData("garbage", OverlayCorner.TopRight)]
    [InlineData(null, OverlayCorner.TopRight)]
    public void ParsesCornerWithFallback(string? text, OverlayCorner expected)
        => Assert.Equal(expected, OverlayPlacement.ParseCorner(text));
}
