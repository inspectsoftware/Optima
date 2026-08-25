using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.Core.Statistics;

/// <summary>
/// Compares two groups of sessions (§14) with a noise guard: a difference is reported as
/// meaningful only when a Welch t-test over per-second FPS samples clears ~95% confidence
/// AND the effect is at least 2% of the baseline average (guards against trivially "significant"
/// micro-differences on huge sample counts).
/// </summary>
public static class BenchmarkComparer
{
    public static BenchmarkComparison Compare(
        string profileA, IReadOnlyList<SessionRecord> sessionsA,
        string profileB, IReadOnlyList<SessionRecord> sessionsB)
    {
        var samplesA = sessionsA.SelectMany(s => s.FpsSamples).Where(double.IsFinite).ToArray();
        var samplesB = sessionsB.SelectMany(s => s.FpsSamples).Where(double.IsFinite).ToArray();

        var statsA = AggregateStats(sessionsA);
        var statsB = AggregateStats(sessionsB);
        var delta = statsB.AverageFps - statsA.AverageFps;

        var meaningful = false;
        string verdict;
        if (samplesA.Length < 30 || samplesB.Length < 30)
        {
            verdict = "Not enough data for a reliable comparison — play a few minutes under each profile.";
        }
        else
        {
            var t = Math.Abs(WelchT(samplesA, samplesB));
            var relativeEffect = statsA.AverageFps > 0 ? Math.Abs(delta) / statsA.AverageFps : 0;
            meaningful = t > 1.96 && relativeEffect >= 0.02;
            verdict = meaningful
                ? $"{(delta > 0 ? profileB : profileA)} is measurably faster ({Math.Abs(delta):F1} FPS average difference)."
                : "The measured difference is within run-to-run noise — no real advantage detected.";
        }

        return new BenchmarkComparison
        {
            ProfileA = profileA,
            ProfileB = profileB,
            StatsA = statsA,
            StatsB = statsB,
            AverageFpsDelta = delta,
            IsStatisticallyMeaningful = meaningful,
            Verdict = verdict,
        };
    }

    /// <summary>Welch's t statistic for two independent samples with unequal variances.</summary>
    public static double WelchT(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count < 2 || b.Count < 2)
        {
            return 0;
        }

        var meanA = a.Average();
        var meanB = b.Average();
        var varA = a.Sum(x => (x - meanA) * (x - meanA)) / (a.Count - 1);
        var varB = b.Sum(x => (x - meanB) * (x - meanB)) / (b.Count - 1);
        var denom = Math.Sqrt(varA / a.Count + varB / b.Count);
        return denom == 0 ? 0 : (meanA - meanB) / denom;
    }

    /// <summary>Sample-count weighted aggregate of several sessions' stats.</summary>
    public static SessionStats AggregateStats(IReadOnlyList<SessionRecord> sessions)
    {
        var withData = sessions.Where(s => s.Stats.HasData).ToArray();
        if (withData.Length == 0)
        {
            return SessionStats.Empty;
        }

        double totalSamples = withData.Sum(s => (double)s.Stats.SampleCount);
        double W(Func<SessionStats, double> f) => withData.Sum(s => f(s.Stats) * s.Stats.SampleCount) / totalSamples;

        return new SessionStats
        {
            SampleCount = (int)Math.Min(int.MaxValue, totalSamples),
            AverageFps = W(s => s.AverageFps),
            OnePercentLowFps = W(s => s.OnePercentLowFps),
            PointOnePercentLowFps = W(s => s.PointOnePercentLowFps),
            AverageFrametimeMs = W(s => s.AverageFrametimeMs),
            P95FrametimeMs = W(s => s.P95FrametimeMs),
            P99FrametimeMs = W(s => s.P99FrametimeMs),
        };
    }
}
