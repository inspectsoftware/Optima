using Optima.Core.Statistics;
using Xunit;

namespace Optima.Tests.Statistics;

public sealed class SeriesEnvelopeTests
{
    [Fact]
    public void ASeriesThatFitsIsDrawnWhole()
    {
        var indices = new List<int> { 99 };
        SeriesEnvelope.Select(Enumerable.Range(0, 120).Select(i => (double)i).ToList(), 60, indices);
        Assert.Equal(Enumerable.Range(0, 120), indices);

        SeriesEnvelope.Select([1, 2, 3], 0, indices);
        Assert.Equal([0, 1, 2], indices);
    }

    [Fact]
    public void ALongSeriesKeepsItsSpikesAndBothEnds()
    {
        // An hour of per-second samples with one dip and one spike, each a single sample wide.
        var values = Enumerable.Range(0, 3600).Select(i => 120 + Math.Sin(i / 40.0)).ToArray();
        values[1234] = 5;
        values[2500] = 400;

        var indices = new List<int>();
        SeriesEnvelope.Select(values, 300, indices);

        Assert.InRange(indices.Count, 300, 2 * 300 + 2);
        Assert.Equal(0, indices[0]);
        Assert.Equal(3599, indices[^1]);
        Assert.Contains(1234, indices);
        Assert.Contains(2500, indices);
        // Ascending without repeats: the line never doubles back.
        Assert.Equal(indices.Distinct().Order(), indices);
    }

    [Fact]
    public void EveryColumnKeepsItsLowestAndHighestSample()
    {
        var random = new Random(7);
        var values = Enumerable.Range(0, 1000).Select(_ => random.NextDouble()).ToArray();

        var indices = new List<int>();
        SeriesEnvelope.Select(values, 100, indices);

        for (var column = 0; column < 100; column++)
        {
            var bucket = values.Skip(column * 10).Take(10).ToArray();
            var drawn = indices.Where(i => i / 10 == column).Select(i => values[i]).ToArray();
            Assert.Contains(bucket.Min(), drawn);
            Assert.Contains(bucket.Max(), drawn);
        }
    }
}
