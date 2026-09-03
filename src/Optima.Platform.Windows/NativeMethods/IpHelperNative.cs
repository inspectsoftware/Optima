using System.Net;
using System.Runtime.InteropServices;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Read-only view of the IPv4 TCP connection table with owning pids (iphlpapi).</summary>
internal static class IpHelperNative
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int MibTcpStateEstablished = 5;
    private const int ErrorInsufficientBuffer = 122;

    internal sealed record TcpConnection(int OwningPid, IPAddress RemoteAddress, int RemotePort, bool Established);

    internal static IReadOnlyList<TcpConnection> GetTcpConnections()
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, sort: false, AfInet, TcpTableOwnerPidAll, 0);
        if (size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, sort: false, AfInet, TcpTableOwnerPidAll, 0);
            if (result == ErrorInsufficientBuffer)
            {
                return [];
            }
            if (result != 0)
            {
                return [];
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + sizeof(int);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var connections = new List<TcpConnection>(rowCount);
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr + i * rowSize);
                connections.Add(new TcpConnection(
                    (int)row.OwningPid,
                    new IPAddress(row.RemoteAddr),
                    (ushort)IPAddress.NetworkToHostOrder((short)(row.RemotePort & 0xFFFF)),
                    row.State == MibTcpStateEstablished));
                }
            return connections;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool sort, int ipVersion, int tableClass, int reserved);
}
