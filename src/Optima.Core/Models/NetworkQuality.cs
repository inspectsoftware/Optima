namespace Optima.Core.Models;

/// <summary>One live network probe result (ping round trip against one target).</summary>
public sealed record NetworkQualitySample
{
    public double PingMs { get; init; }
    public double JitterMs { get; init; }
    public double PacketLossPct { get; init; }

    /// <summary>The host the sample was measured against.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// True when measuring the fallback reference host rather than a discovered game endpoint;
    /// the readout then reflects link quality, not game-server latency.
    /// </summary>
    public bool IsReferenceHost { get; init; }
}

/// <summary>Whole-session network quality aggregate persisted with the session row.</summary>
public sealed record NetworkQualityStats
{
    public double AveragePingMs { get; init; }
    public double JitterMs { get; init; }
    public double PacketLossPct { get; init; }
    public int SampleCount { get; init; }

    public bool HasData => SampleCount > 0;
}
