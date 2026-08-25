using COPSBootstrapper.Core.Statistics;
using Xunit;

namespace COPSBootstrapper.Tests.Statistics;

public class FrametimeStatisticsTests
{
    [Fact]
    public void Compute_EmptyInput_ReturnsEmptyStats()
    {
        var stats = FrametimeStatistics.Compute([]);
        Assert.False(stats.HasData);
        Assert.Equal(0, stats.AverageFps);
    }

    [Fact]
    public void Compute_UniformFrametimes_MatchesExpectedFps()
    {
        // 10 ms per frame = 100 FPS everywhere.
        var frametimes = Enumerable.Repeat(10.0, 1000).ToArray();
        var stats = FrametimeStatistics.Compute(frametimes);

        Assert.Equal(1000, stats.SampleCount);
        Assert.Equal(100, stats.AverageFps, 3);
        Assert.Equal(100, stats.OnePercentLowFps, 3);
        Assert.Equal(10, stats.P99FrametimeMs, 3);
    }

    [Fact]
    public void Compute_WithStutterSpikes_LowsFallBelowAverage()
    {
        // 990 fast frames at 5 ms and 10 slow frames at 50 ms.
        var frametimes = Enumerable.Repeat(5.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToArray();
        var stats = FrametimeStatistics.Compute(frametimes);

        Assert.True(stats.OnePercentLowFps < stats.AverageFps);
        // The slowest 1% (10 frames) are exactly the 50 ms frames → 20 FPS.
        Assert.Equal(20, stats.OnePercentLowFps, 3);
        Assert.True(stats.P99FrametimeMs >= 5);
    }

    [Fact]
    public void Compute_IgnoresInvalidSamples()
    {
        var stats = FrametimeStatistics.Compute([10, -5, double.NaN, double.PositiveInfinity, 10]);
        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(100, stats.AverageFps, 3);
    }

    [Theory]
    [InlineData(0.5, 3.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 5.0)]
    public void Percentile_InterpolatesLinearly(double p, double expected)
    {
        double[] sorted = [1, 2, 3, 4, 5];
        Assert.Equal(expected, FrametimeStatistics.Percentile(sorted, p), 6);
    }

    [Fact]
    public void Percentile_SingleElement_ReturnsThatElement()
    {
        Assert.Equal(42, FrametimeStatistics.Percentile([42.0], 0.99));
    }
}
