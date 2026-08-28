using System.Net;
using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>
/// Discovers the remote endpoints a set of processes talk to. Read-only observation of the
/// connection table; no packets are touched (§ security boundaries).
/// </summary>
public interface IRemoteEndpointSource
{
    /// <summary>Distinct public remote addresses of established connections owned by the given pids.</summary>
    Task<IReadOnlyList<IPAddress>> GetRemoteEndpointsAsync(IReadOnlyList<int> processIds, CancellationToken ct = default);
}

/// <summary>
/// Passive network quality measurement: ICMP pings against the game's own endpoints when they
/// answer, a configurable reference host otherwise. Runs non-elevated in the app process.
/// </summary>
public interface INetworkQualityMonitor : IAsyncDisposable
{
    /// <summary>Most recent sample; null before the first ping completes.</summary>
    NetworkQualitySample? Latest { get; }

    /// <summary>Raised about once per second from a background thread.</summary>
    event EventHandler<NetworkQualitySample>? SampleArrived;

    /// <summary>Starts measuring; endpoint discovery uses the given process ids.</summary>
    Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default);

    /// <summary>Stops measuring and returns the whole-session aggregate; null when nothing was measured.</summary>
    Task<NetworkQualityStats?> StopAsync();
}
