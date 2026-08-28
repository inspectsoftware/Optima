using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class AsciiSparklineTests
{
    [Fact]
    public void EmptyInputRendersEmpty()
    {
        Assert.Equal(string.Empty, AsciiSparkline.Render([], 20));
        Assert.Empty(AsciiSparkline.RenderWrapped([], 20));
    }

    [Fact]
    public void ZeroWidthRendersEmpty()
        => Assert.Equal(string.Empty, AsciiSparkline.Render([1, 2, 3], 0));

    [Fact]
    public void ShortSeriesRendersOneCharPerValue()
    {
        var result = AsciiSparkline.Render([0, 50, 100], 20);
        Assert.Equal(3, result.Length);
        Assert.Equal('▁', result[0]);
        Assert.Equal('█', result[2]);
    }

    [Fact]
    public void MonotonicSeriesRendersNonDecreasingLevels()
    {
        var values = Enumerable.Range(0, 10).Select(i => (double)i).ToList();
        var result = AsciiSparkline.Render(values, 10);
        for (var i = 1; i < result.Length; i++)
        {
            Assert.True(result[i] >= result[i - 1]);
        }
    }

    [Fact]
    public void LongSeriesIsDownsampledToWidth()
    {
        var values = Enumerable.Range(0, 500).Select(i => (double)i).ToList();
        var result = AsciiSparkline.Render(values, 40);
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void FlatSeriesRendersMidLevel()
    {
        var result = AsciiSparkline.Render([60, 60, 60], 10);
        Assert.All(result, c => Assert.Equal('▅', c));
    }

    [Fact]
    public void WrappedRenderingSharesOneScale()
    {
        // 250 values: first line low, second line high; the max lives on line 2, so line 1
        // must contain no full block if scaling is shared.
        var values = Enumerable.Repeat(10.0, 120).Concat(Enumerable.Repeat(100.0, 130)).ToList();
        var lines = AsciiSparkline.RenderWrapped(values, 120);
        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain('█', lines[0]);
        Assert.Contains('█', lines[1]);
    }
}
