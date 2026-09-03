using System.Net;
using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Discovers the remote endpoints a set of processes talk to.</summary>
public interface IRemoteEndpointSource
{
    Task<IReadOnlyList<IPAddress>> GetRemoteEndpointsAsync(IReadOnlyList<int> processIds, CancellationToken ct = default);
}

/// <summary>Passive network quality measurement: ICMP pings against the game's own endpoints when they answer, a configurable reference host otherwise.</summary>
public interface INetworkQualityMonitor : IAsyncDisposable
{
    NetworkQualitySample? Latest { get; }

    event EventHandler<NetworkQualitySample>? SampleArrived;

    Task StartAsync(IReadOnlyList<int> processIds, CancellationToken ct = default);

    Task<NetworkQualityStats?> StopAsync();
}
