using System.Runtime.InteropServices;

namespace Optima.Elevated;

/// <summary>
/// Creates and removes root-enumerated device nodes via SetupAPI, the work `devcon install`
/// does, done in-process so no WDK tool has to be redistributed.
///
/// This is required because an IddCx virtual display is enumerated by ROOT rather than by a
/// bus: staging the package with pnputil alone installs the driver but never produces a
/// device, so nothing appears until the node is created explicitly against its hardware id.
/// </summary>
internal static class DeviceInstaller
{
    // {4d36e968-e325-11ce-bfc1-08002be10318} is the Display device class.
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private const uint DICD_GENERATE_ID = 0x00000001;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint DIF_REMOVE = 0x00000005;
    private const uint DIGCF_PRESENT = 0x00000002;
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
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

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

    /// <summary>
    /// Creates the device node for <paramref name="hardwareId"/> and binds the driver in
    /// <paramref name="infPath"/> to it. Idempotent: an existing node is reused.
    /// </summary>
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

            // Hardware id is REG_MULTI_SZ: the id, then a terminating empty string.
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

        // Bind the staged driver to the freshly created node.
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out var rebootRequired))
        {
            // SetupAPI reports failures as 0xE0000xxx codes; surface the raw value rather
            // than guessing at a meaning, since the common causes all look alike here.
            var code = Marshal.GetLastWin32Error();
            return (false, false,
                $"Binding the driver to the device failed (0x{code:X8}). The package is most likely unsigned, "
                + "signed by a publisher this machine does not trust, or not applicable to this version of Windows.");
        }

        return (true, rebootRequired, string.Empty);
    }

    /// <summary>Removes every device node carrying <paramref name="hardwareId"/>.</summary>
    internal static (bool Success, int Removed, string Error) RemoveRootDevices(string hardwareId)
    {
        var classGuid = DisplayClassGuid;
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return (false, 0, $"SetupDiGetClassDevs failed ({Marshal.GetLastWin32Error()}).");
        }

        var removed = 0;
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

                if (!MatchesHardwareId(deviceInfoSet, ref deviceInfoData, hardwareId))
                {
                    continue;
                }

                if (SetupDiCallClassInstaller(DIF_REMOVE, deviceInfoSet, ref deviceInfoData))
                {
                    removed++;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return (true, removed, string.Empty);
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
        return bytes; // trailing four zero bytes terminate the string and the list
    }

    private static IEnumerable<string> ParseMultiSz(byte[] buffer)
        => System.Text.Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
