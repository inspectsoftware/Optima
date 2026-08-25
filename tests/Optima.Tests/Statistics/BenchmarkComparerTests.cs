using Optima.Core.Models;
using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public class BenchmarkComparerTests
{
    private static SessionRecord Session(string profile, double avgFps, IReadOnlyList<double> samples) => new()
    {
        ProfileName = profile,
        GamePackageId = "test.pkg",
        Stats = new SessionStats { AverageFps = avgFps, SampleCount = samples.Count },
        FpsSamples = samples,
    };

    private static double[] Noisy(double mean, int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => mean + (random.NextDouble() - 0.5) * 4).ToArray();
    }

    [Fact]
    public void Compare_ClearDifference_IsMeaningful()
    {
        var a = Session("A", 150, Noisy(150, 300, 1));
        var b = Session("B", 190, Noisy(190, 300, 2));

        var result = BenchmarkComparer.Compare("A", [a], "B", [b]);

        Assert.True(result.IsStatisticallyMeaningful);
        Assert.True(result.AverageFpsDelta > 30);
        Assert.Contains("B", result.Verdict);
    }

    [Fact]
    public void Compare_TinyDifference_IsNotMeaningful()
    {
        // Within the 2% effect-size guard even if statistically detectable.
        var a = Session("A", 180.0, Noisy(180.0, 500, 3));
        var b = Session("B", 181.0, Noisy(181.0, 500, 4));

        var result = BenchmarkComparer.Compare("A", [a], "B", [b]);

        Assert.False(result.IsStatisticallyMeaningful);
        Assert.Contains("noise", result.Verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_TooFewSamples_AsksForMoreData()
    {
        var a = Session("A", 100, [100, 101]);
        var b = Session("B", 200, [200, 201]);

        var result = BenchmarkComparer.Compare("A", [a], "B", [b]);

        Assert.False(result.IsStatisticallyMeaningful);
        Assert.Contains("Not enough data", result.Verdict);
    }

    [Fact]
    public void AggregateStats_WeightsBySampleCount()
    {
        var small = Session("A", 100, []) with
        {
            Stats = new SessionStats { AverageFps = 100, SampleCount = 100 },
        };
        var large = Session("A", 200, []) with
        {
            Stats = new SessionStats { AverageFps = 200, SampleCount = 300 },
        };

        var aggregate = BenchmarkComparer.AggregateStats([small, large]);

        Assert.Equal(175, aggregate.AverageFps, 3);
    }

    [Fact]
    public void WelchT_IdenticalSamples_IsZero()
    {
        double[] samples = [10, 12, 11, 13, 9];
        Assert.Equal(0, BenchmarkComparer.WelchT(samples, samples), 6);
    }
}
