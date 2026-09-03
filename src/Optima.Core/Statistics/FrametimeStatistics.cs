using Optima.Core.Models;

namespace Optima.Core.Statistics;

/// <summary>Pure math over frametime samples (§13): average / percentile-low FPS and frametime percentiles.</summary>
public static class FrametimeStatistics
{
    public static SessionStats Compute(IReadOnlyList<double> frametimesMs)
    {
        var valid = frametimesMs.Where(t => t > 0 && double.IsFinite(t)).ToArray();
        if (valid.Length == 0)
        {
            return SessionStats.Empty;
        }

        Array.Sort(valid);

        var avgFrametime = valid.Average();

        return new SessionStats
        {
            SampleCount = valid.Length,
            AverageFrametimeMs = avgFrametime,
            AverageFps = 1000.0 / avgFrametime,
            P95FrametimeMs = Percentile(valid, 0.95),
            P99FrametimeMs = Percentile(valid, 0.99),
            OnePercentLowFps = LowFps(valid, 0.01),
            PointOnePercentLowFps = LowFps(valid, 0.001),
        };
    }

    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }
        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = p * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedAscending[lower];
        }
        var weight = rank - lower;
        return sortedAscending[lower] * (1 - weight) + sortedAscending[upper] * weight;
    }

    private static double LowFps(double[] sortedAscendingFrametimes, double fraction)
    {
        var count = Math.Max(1, (int)Math.Floor(sortedAscendingFrametimes.Length * fraction));
        var slowest = sortedAscendingFrametimes.AsSpan(sortedAscendingFrametimes.Length - count, count);
        double sum = 0;
        foreach (var t in slowest)
        {
            sum += t;
        }
        var avg = sum / count;
        return avg <= 0 ? 0 : 1000.0 / avg;
    }
}
