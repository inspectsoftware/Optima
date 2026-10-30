using System.Runtime.InteropServices;
using Optima.Core.Detection;

namespace Optima.Watchdog;

/// <summary>
/// Creates and removes root-enumerated device nodes via SetupAPI, the work `devcon install` does, done in-process so no
/// WDK tool has to be redistributed.
/// </summary>
internal static class DeviceInstaller
{
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private const uint DICD_GENERATE_ID = 0x00000001;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint DIF_REMOVE = 0x00000005;
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint SPDRP_DRIVER = 0x00000009;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint CM_DISABLE_UI_NOT_OK = 0x00000004;
    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private const int ERROR_NO_MORE_ITEMS = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, char[] buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, byte[] buffer, uint bufferSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    private delegate void NodeAction(IntPtr set, ref SP_DEVINFO_DATA data);

    /// <summary>Enables a device by instance id. Returns the CONFIGRET, 0 on success.</summary>
    internal static int EnableDevice(string instanceId)
    {
        var located = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
        return located != 0 ? located : CM_Enable_DevNode(devInst, 0);
    }

    /// <summary>
    /// Disables then enables a device once, which makes Windows start the driver again: what Device
    /// Manager's disable/enable does for a node stuck after a failed start. The enable runs even when
    /// the disable was refused, so a restart never leaves the device switched off.
    /// Returns both CONFIGRETs, 0 on success.
    /// </summary>
    internal static (int Disable, int Enable) RestartDevice(string instanceId)
    {
        var located = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
        if (located != 0)
        {
            return (located, located);
        }
        var disabled = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK);
        return (disabled, CM_Enable_DevNode(devInst, 0));
    }

    /// <summary>
    /// Every present root node carrying the hardware id, with its Device Manager problem code (0 when
    /// working) and whether a driver is bound to it. Root nodes of any class are included, because a
    /// node whose driver install failed can be left without the Display class.
    /// </summary>
    internal static IReadOnlyList<VddRootNodes.Node> GetRootDevices(string hardwareId)
    {
        var nodes = new List<VddRootNodes.Node>();
        ForEachRootDevice(hardwareId, (IntPtr set, ref SP_DEVINFO_DATA data) =>
        {
            var buffer = new char[400];
            var instanceId = SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out var length)
                ? new string(buffer, 0, Math.Max(0, length - 1))
                : $"devinst {data.DevInst}";

            // -1 when Windows will not say, so an unreadable node is never taken for a working one.
            var problem = CM_Get_DevNode_Status(out var status, out var number, data.DevInst, 0) != 0 ? -1
                : (status & DN_HAS_PROBLEM) != 0 ? (int)number
                : 0;

            // A bound driver has a driver key; a node Windows found no driver for has none.
            SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_DRIVER, out _, null, 0, out var driverKeySize);
            nodes.Add(new VddRootNodes.Node(instanceId, problem, HasDriver: driverKeySize > 0));
        });
        return nodes;
    }

    internal static (bool Success, bool RebootRequired, string Error) CreateRootDevice(string hardwareId, string infPath)
    {
        var classGuid = DisplayClassGuid;
        var deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return (false, false, $"SetupDiCreateDeviceInfoList failed ({Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var deviceInfoData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(deviceInfoSet, "Display", ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref deviceInfoData))
            {
                return (false, false, $"SetupDiCreateDeviceInfo failed ({Marshal.GetLastWin32Error()}).");
            }

            var idBuffer = MultiSz(hardwareId);
            if (!SetupDiSetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, SPDRP_HARDWAREID, idBuffer, (uint)idBuffer.Length))
            {
                return (false, false, $"Setting the hardware id failed ({Marshal.GetLastWin32Error()}).");
            }

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, deviceInfoSet, ref deviceInfoData))
            {
                return (false, false, $"Registering the device failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out var rebootRequired))
        {
            var code = Marshal.GetLastWin32Error();
            return (false, false,
                $"Binding the driver to the device failed (0x{code:X8}). The package is most likely unsigned, "
                + "signed by a publisher this machine does not trust, or not applicable to this version of Windows.");
        }

        return (true, rebootRequired, string.Empty);
    }

    internal static (bool Success, int Removed, string Error) RemoveRootDevices(string hardwareId)
    {
        var removed = 0;
        var opened = ForEachRootDevice(hardwareId, (IntPtr set, ref SP_DEVINFO_DATA data) =>
        {
            if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref data))
            {
                removed++;
            }
        });
        return opened
            ? (true, removed, string.Empty)
            : (false, 0, $"SetupDiGetClassDevs failed ({Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every present root node carrying the hardware id, whatever its
    /// class. False when the device list could not be opened.
    /// </summary>
    private static bool ForEachRootDevice(string hardwareId, NodeAction action)
    {
        var deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfoData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }
                    continue;
                }

                if (MatchesHardwareId(deviceInfoSet, ref deviceInfoData, hardwareId))
                {
                    action(deviceInfoSet, ref deviceInfoData);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
        return true;
    }

    private static bool MatchesHardwareId(IntPtr set, ref SP_DEVINFO_DATA data, string hardwareId)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, null, 0, out var required);
        if (required == 0)
        {
            return false;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, out _, buffer, required, out _))
        {
            return false;
        }

        foreach (var id in ParseMultiSz(buffer))
        {
            if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] MultiSz(string value)
    {
        var bytes = new byte[(value.Length + 2) * 2];
        System.Text.Encoding.Unicode.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }

    private static IEnumerable<string> ParseMultiSz(byte[] buffer)
        => System.Text.Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
