using Optima.Core.Models;

namespace Optima.Core.Statistics;

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
            verdict = "Not enough data for a reliable comparison. Play a few minutes under each profile.";
        }
        else
        {
            var t = Math.Abs(WelchT(samplesA, samplesB));
            var relativeEffect = statsA.AverageFps > 0 ? Math.Abs(delta) / statsA.AverageFps : 0;
            meaningful = t > 1.96 && relativeEffect >= 0.02;
            verdict = meaningful
                ? $"{(delta > 0 ? profileB : profileA)} is measurably faster ({Math.Abs(delta):F1} FPS average difference)."
                : "The measured difference is within run-to-run noise, so no real advantage detected.";
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

    /// <summary>
    /// Per-run comparison for the guided benchmark: each run's average FPS is one observation.
    /// The pooled comparison treats every per-second sample as independent, which inflates n by
    /// orders of magnitude on autocorrelated frame data; runs are the honest unit of analysis.
    /// Uses the Welch-Satterthwaite degrees of freedom with a t critical-value table, since n
    /// is small here and 1.96 would be far too permissive.
    /// </summary>
    public static PerRunComparison ComparePerRun(
        string profileA, IReadOnlyList<SessionRecord> sessionsA,
        string profileB, IReadOnlyList<SessionRecord> sessionsB)
    {
        var runsA = sessionsA.Where(s => s.Stats.HasData).Select(s => s.Stats.AverageFps).ToArray();
        var runsB = sessionsB.Where(s => s.Stats.HasData).Select(s => s.Stats.AverageFps).ToArray();
        var meanA = runsA.Length > 0 ? runsA.Average() : 0;
        var meanB = runsB.Length > 0 ? runsB.Average() : 0;
        var delta = meanB - meanA;

        if (runsA.Length < 3 || runsB.Length < 3)
        {
            return new PerRunComparison
            {
                ProfileA = profileA,
                ProfileB = profileB,
                RunsA = runsA.Length,
                RunsB = runsB.Length,
                MeanFpsA = meanA,
                MeanFpsB = meanB,
                AverageFpsDelta = delta,
                Verdict = "At least 3 completed runs per profile are needed for a per-run verdict.",
            };
        }

        var t = WelchT(runsA, runsB);
        var df = WelchSatterthwaiteDf(runsA, runsB);
        var critical = TCritical(df);
        var relativeEffect = meanA > 0 ? Math.Abs(delta) / meanA : 0;
        var meaningful = Math.Abs(t) > critical && relativeEffect >= 0.02;
        var underpowered = runsA.Length < 5 || runsB.Length < 5;

        var verdict = meaningful
            ? $"{(delta > 0 ? profileB : profileA)} is faster across runs ({Math.Abs(delta):F1} FPS mean difference over {runsA.Length}+{runsB.Length} runs)."
            : "The per-run difference is within run-to-run noise, so no real advantage detected.";
        if (underpowered)
        {
            verdict += " Underpowered: fewer than 5 runs per side, treat this as directional only.";
        }

        return new PerRunComparison
        {
            ProfileA = profileA,
            ProfileB = profileB,
            RunsA = runsA.Length,
            RunsB = runsB.Length,
            MeanFpsA = meanA,
            MeanFpsB = meanB,
            AverageFpsDelta = delta,
            TStatistic = t,
            DegreesOfFreedom = df,
            IsStatisticallyMeaningful = meaningful,
            IsUnderpowered = underpowered,
            Verdict = verdict,
        };
    }

    /// <summary>Welch-Satterthwaite effective degrees of freedom.</summary>
    public static double WelchSatterthwaiteDf(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count < 2 || b.Count < 2)
        {
            return 1;
        }
        var meanA = a.Average();
        var meanB = b.Average();
        var varA = a.Sum(x => (x - meanA) * (x - meanA)) / (a.Count - 1);
        var varB = b.Sum(x => (x - meanB) * (x - meanB)) / (b.Count - 1);
        var fracA = varA / a.Count;
        var fracB = varB / b.Count;
        var denominator = fracA * fracA / (a.Count - 1) + fracB * fracB / (b.Count - 1);
        if (denominator == 0)
        {
            return a.Count + b.Count - 2;
        }
        var df = (fracA + fracB) * (fracA + fracB) / denominator;
        return Math.Max(1, df);
    }

    // Two-tailed critical values of Student's t at alpha = 0.05, df 1..30.
    private static readonly double[] TTable =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    /// <summary>Critical |t| at ~95% confidence; flooring df keeps the test conservative.</summary>
    public static double TCritical(double df)
    {
        var index = (int)Math.Floor(df);
        if (index < 1)
        {
            index = 1;
        }
        return index <= TTable.Length ? TTable[index - 1] : 1.96;
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
