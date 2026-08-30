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

/// <summary>How a session was started; recorded so history stays attributable.</summary>
public enum LaunchKind
{
    /// <summary>The PLAY button launched the game through Optima.</summary>
    Play,
    /// <summary>Watch mode attached to a game started outside Optima.</summary>
    Watch,
    /// <summary>A run inside the guided benchmark flow.</summary>
    Benchmark,
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

    /// <summary>Catalog ids of the tweaks that were enabled while this session ran.</summary>
    public IReadOnlyList<string> TweakIds { get; init; } = [];

    /// <summary>Content hash of the profile's settings, so trends survive profile renames and expose edits.</summary>
    public string ProfileHash { get; init; } = string.Empty;

    public LaunchKind LaunchKind { get; init; } = LaunchKind.Play;

    /// <summary>Network quality aggregate; null when the session was not measured.</summary>
    public NetworkQualityStats? Network { get; init; }

    /// <summary>Per-mode k/d/a/w/l gained during this session, from public-profile deltas;
    /// null when the player name is not configured or the API was unreachable.</summary>
    public Stats.CopsProfileDelta? StatsDelta { get; init; }

    /// <summary>Critical Ops version at session time, once the update feed tracks it (v0.4).</summary>
    public string? GameVersion { get; init; }
}

/// <summary>Per-run benchmark result: each completed run's average FPS is one observation (§14).</summary>
public sealed record PerRunComparison
{
    public required string ProfileA { get; init; }
    public required string ProfileB { get; init; }
    public int RunsA { get; init; }
    public int RunsB { get; init; }
    public double MeanFpsA { get; init; }
    public double MeanFpsB { get; init; }
    public double AverageFpsDelta { get; init; }
    public double TStatistic { get; init; }
    public double DegreesOfFreedom { get; init; }
    public bool IsStatisticallyMeaningful { get; init; }

    /// <summary>Fewer than 5 runs per side: the verdict is directional at best.</summary>
    public bool IsUnderpowered { get; init; }

    public string Verdict { get; init; } = string.Empty;
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
