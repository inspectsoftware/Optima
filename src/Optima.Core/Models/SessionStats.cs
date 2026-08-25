namespace Optima.Core.Models;

/// <summary>Aggregated frametime statistics for a play session or benchmark run (§13/§14).</summary>
public sealed record SessionStats
{
    public double AverageFps { get; init; }
    public double OnePercentLowFps { get; init; }
    public double PointOnePercentLowFps { get; init; }
    public double AverageFrametimeMs { get; init; }
    public double P95FrametimeMs { get; init; }
    public double P99FrametimeMs { get; init; }
    public int SampleCount { get; init; }

    public static SessionStats Empty { get; } = new();

    public bool HasData => SampleCount > 0;
}

/// <summary>A completed session persisted to history.</summary>
public sealed record SessionRecord
{
    public long Id { get; init; }
    public required string ProfileName { get; init; }
    public required string GamePackageId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public SessionStats Stats { get; init; } = SessionStats.Empty;

    /// <summary>Per-second FPS samples kept for benchmark significance testing.</summary>
    public IReadOnlyList<double> FpsSamples { get; init; } = [];
}

/// <summary>Result of comparing two groups of sessions (benchmark mode, §14).</summary>
public sealed record BenchmarkComparison
{
    public required string ProfileA { get; init; }
    public required string ProfileB { get; init; }
    public SessionStats StatsA { get; init; } = SessionStats.Empty;
    public SessionStats StatsB { get; init; } = SessionStats.Empty;
    public double AverageFpsDelta { get; init; }

    /// <summary>True only when the difference exceeds run-to-run noise (Welch test).</summary>
    public bool IsStatisticallyMeaningful { get; init; }

    public string Verdict { get; init; } = string.Empty;
}
