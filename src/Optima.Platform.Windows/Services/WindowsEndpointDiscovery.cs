using System.Net;
using Optima.Core.Abstractions;
using Optima.Core.Network;
using Optima.Platform.Windows.NativeMethods;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Remote endpoints of the game processes, read from the IPv4 TCP table. The game's actual
/// traffic is often UDP (whose table has no remote addresses), so an empty result is normal;
/// the monitor then falls back to the reference host.
/// </summary>
public sealed class WindowsEndpointDiscovery : IRemoteEndpointSource
{
    public Task<IReadOnlyList<IPAddress>> GetRemoteEndpointsAsync(IReadOnlyList<int> processIds, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<IPAddress>>(() =>
        {
            var pids = processIds.ToHashSet();
            return IpHelperNative.GetTcpConnections()
                .Where(c => c.Established && pids.Contains(c.OwningPid) && EndpointFilter.IsPublicUnicast(c.RemoteAddress))
                .Select(c => c.RemoteAddress)
                .Distinct()
                .ToList();
        }, ct);
}
