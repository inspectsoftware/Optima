using System.Net;
using System.Net.Sockets;

namespace Optima.Core.Network;

/// <summary>
/// Keeps only addresses that can plausibly be a game server: public unicast IPv4.
/// Private, loopback, link-local, carrier-NAT and multicast ranges are dropped, since
/// pinging those says nothing about the game connection.
/// </summary>
public static class EndpointFilter
{
    public static bool IsPublicUnicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 => false,
            10 => false,                              // 10/8 private
            127 => false,                             // loopback
            100 when b[1] >= 64 && b[1] <= 127 => false, // 100.64/10 carrier NAT
            169 when b[1] == 254 => false,            // link local
            172 when b[1] >= 16 && b[1] <= 31 => false,  // 172.16/12 private
            192 when b[1] == 168 => false,            // 192.168/16 private
            >= 224 => false,                          // multicast + reserved
            _ => true,
        };
    }
}
