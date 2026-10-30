using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>
/// Native Wifi (wlanapi.dll): a client session with the WLAN AutoConfig service, its interfaces, and
/// the two interface settings Boost asks for. These functions return their error code rather than
/// setting the last error.
/// </summary>
public static class WlanNative
{
    private const uint ERROR_SUCCESS = 0;
    // WLAN AutoConfig is not running, which is what a PC without a Wi-Fi adapter usually looks like.
    private const uint ERROR_SERVICE_NOT_ACTIVE = 1062;
    // The client version for Windows Vista and later.
    private const uint ClientVersion = 2;
    private const int wlan_interface_state_connected = 1;

    // WLAN_INTF_OPCODE. Both are documented as per-client: background scanning is off "if at least one
    // client disables it" and media streaming mode on "if at least one client enables it", and a
    // disconnect resets both. Neither is a stored interface setting, so each is Optima's request for
    // as long as its session is open, and closing the session (or the process ending) withdraws it.
    // That is why neither needs a ledger.
    internal const int wlan_intf_opcode_background_scan_enabled = 2;
    internal const int wlan_intf_opcode_media_streaming_mode = 3;

    // WLAN_INTERFACE_INFO: the interface's GUID, its description as WCHAR[256], and its state.
    private const int InterfaceInfoSize = 16 + 512 + 4;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out WlanHandle clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(WlanHandle clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(WlanHandle clientHandle, ref Guid interfaceGuid, int opCode, uint dataSize, ref int data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>A WLAN client session, closed with the handle (or by Windows when the process ends).</summary>
    public sealed class WlanHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WlanHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => WlanCloseHandle(handle, IntPtr.Zero) == ERROR_SUCCESS;
    }

    /// <summary>Opens a client session, or null when this PC has no Wi-Fi (no WLAN service, or no wlanapi.dll).</summary>
    public static WlanHandle? Open()
    {
        uint result;
        WlanHandle handle;
        try
        {
            result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle);
        }
        catch (DllNotFoundException)
        {
            // Windows Server without the Wireless LAN feature.
            return null;
        }
        if (result == ERROR_SUCCESS)
        {
            return handle;
        }
        handle.Dispose();
        if (result == ERROR_SERVICE_NOT_ACTIVE)
        {
            return null;
        }
        throw new System.ComponentModel.Win32Exception((int)result, "Opening a Wi-Fi session failed");
    }

    /// <summary>The interfaces connected to a network right now, with their descriptions.</summary>
    public static List<(Guid Id, string Name)> ConnectedInterfaces(WlanHandle session)
    {
        var result = WlanEnumInterfaces(session, IntPtr.Zero, out var list);
        if (result != ERROR_SUCCESS)
        {
            throw new System.ComponentModel.Win32Exception((int)result, "Listing the Wi-Fi interfaces failed");
        }
        try
        {
            // WLAN_INTERFACE_INFO_LIST: two DWORDs (count, index), then the entries.
            var count = Marshal.ReadInt32(list);
            var connected = new List<(Guid, string)>();
            for (var i = 0; i < count; i++)
            {
                var entry = list + 8 + i * InterfaceInfoSize;
                if (Marshal.ReadInt32(entry + 16 + 512) == wlan_interface_state_connected)
                {
                    var id = Marshal.PtrToStructure<Guid>(entry);
                    var name = Marshal.PtrToStringUni(entry + 16, 256);
                    connected.Add((id, name.IndexOf('\0') is var end and >= 0 ? name[..end] : name));
                }
            }
            return connected;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Sets a BOOL interface setting for this session. False when Windows refused it.</summary>
    internal static bool Set(WlanHandle session, Guid interfaceId, int opCode, bool value)
    {
        var data = value ? 1 : 0;
        return WlanSetInterface(session, ref interfaceId, opCode, sizeof(int), ref data, IntPtr.Zero) == ERROR_SUCCESS;
    }
}
