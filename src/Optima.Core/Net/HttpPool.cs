using System.Net;
using System.Net.Http;

namespace Optima.Core.Net;

/// <summary>
/// The one connection pool behind every outbound call Optima makes. Each client used to build its
/// own handler — separate socket pools, separate handshakes, and no decompression, so the stats API
/// answered gzipped and the bytes were transferred raw. The handler is the expensive half of an
/// HttpClient; sharing it is the fix, and clients stay cheap.
/// </summary>
public static class HttpPool
{
    private static readonly Lazy<SocketsHttpHandler> SharedHandler = new(() => new SocketsHttpHandler
    {
        // The public API and the news page both answer compressed.
        AutomaticDecompression = DecompressionMethods.All,
        // A desktop app stays up for days: recycle connections so DNS changes are still picked up.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    /// <summary>
    /// The shared handler. Callers must build their <see cref="HttpClient"/> with
    /// <c>disposeHandler: false</c> so disposing a client never tears down the pool.
    /// </summary>
    public static HttpMessageHandler Shared => SharedHandler.Value;
}
