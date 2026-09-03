using System.Net;
using System.Net.Sockets;

namespace Optima.Core.Network;

/// <summary>Keeps only addresses that can plausibly be a game server: public unicast IPv4.</summary>
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
            10 => false,
            127 => false,
            100 when b[1] >= 64 && b[1] <= 127 => false,
            169 when b[1] == 254 => false,
            172 when b[1] >= 16 && b[1] <= 31 => false,
            192 when b[1] == 168 => false,
            >= 224 => false,
            _ => true,
        };
    }
}
