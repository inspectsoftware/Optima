using Optima.Core.Models;
using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class BenchmarkComparerPerRunTests
{
    private static SessionRecord Run(double avgFps) => new()
    {
        ProfileName = "P",
        GamePackageId = "com.example",
        Stats = new SessionStats { AverageFps = avgFps, SampleCount = 1000 },
    };

    private static IReadOnlyList<SessionRecord> Runs(params double[] fps) => fps.Select(Run).ToList();

    [Fact]
    public void FewerThanThreeRunsRefusesAVerdict()
    {
        var result = BenchmarkComparer.ComparePerRun("A", Runs(100, 101), "B", Runs(120, 121, 122));
        Assert.False(result.IsStatisticallyMeaningful);
        Assert.Contains("3 completed runs", result.Verdict);
    }

    [Fact]
    public void ClearDifferenceIsMeaningful()
    {
        var result = BenchmarkComparer.ComparePerRun(
            "A", Runs(100, 101, 99, 100, 101),
            "B", Runs(150, 151, 149, 150, 151));
        Assert.True(result.IsStatisticallyMeaningful);
        Assert.False(result.IsUnderpowered);
        Assert.Contains("B", result.Verdict);
        Assert.Equal(50, result.AverageFpsDelta, 1);
    }

    [Fact]
    public void NoiseIsNotMeaningful()
    {
        var result = BenchmarkComparer.ComparePerRun(
            "A", Runs(100, 140, 90, 120, 110),
            "B", Runs(105, 135, 95, 118, 112));
        Assert.False(result.IsStatisticallyMeaningful);
        Assert.Contains("noise", result.Verdict);
    }

    [Fact]
    public void ThreeRunsAreDirectionalOnly()
    {
        var result = BenchmarkComparer.ComparePerRun(
            "A", Runs(100, 101, 99),
            "B", Runs(150, 151, 149));
        Assert.True(result.IsUnderpowered);
        Assert.Contains("directional", result.Verdict);
    }

    [Fact]
    public void SmallEffectFailsTheRelativeFloorEvenWhenTIsLarge()
    {
        // 1% effect with near-zero variance: huge t, but below the 2% floor.
        var result = BenchmarkComparer.ComparePerRun(
            "A", Runs(200.0, 200.1, 199.9, 200.0, 200.1),
            "B", Runs(202.0, 202.1, 201.9, 202.0, 202.1));
        Assert.False(result.IsStatisticallyMeaningful);
    }

    [Fact]
    public void WelchSatterthwaiteMatchesKnownAnswer()
    {
        // Equal n and equal variance reduce to df = 2n - 2.
        double[] a = [10, 12, 14, 16];
        double[] b = [20, 22, 24, 26];
        Assert.Equal(6, BenchmarkComparer.WelchSatterthwaiteDf(a, b), 3);
    }

    [Fact]
    public void TCriticalUsesTheTableAndFloorsDf()
    {
        Assert.Equal(12.706, BenchmarkComparer.TCritical(1), 3);
        Assert.Equal(2.776, BenchmarkComparer.TCritical(4.9), 3);   // floors to 4
        Assert.Equal(2.042, BenchmarkComparer.TCritical(30), 3);
        Assert.Equal(1.96, BenchmarkComparer.TCritical(200), 3);
        Assert.Equal(12.706, BenchmarkComparer.TCritical(0.4), 3);  // clamps up to df 1
    }

    [Fact]
    public void RunsWithoutDataAreIgnored()
    {
        var noData = new SessionRecord { ProfileName = "P", GamePackageId = "g" };
        var result = BenchmarkComparer.ComparePerRun(
            "A", [Run(100), Run(101), Run(99), noData],
            "B", Runs(150, 151, 149));
        Assert.Equal(3, result.RunsA);
    }
}
