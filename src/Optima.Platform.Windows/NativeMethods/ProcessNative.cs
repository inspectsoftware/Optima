using System.Runtime.InteropServices;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Documented process QoS APIs: power throttling (EcoQoS) via Get/SetProcessInformation.</summary>
public static class ProcessNative
{
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS

    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE information, int informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE information, int informationSize);

    /// <summary>True when EcoQoS execution-speed throttling is currently forced on for the process.</summary>
    public static bool IsPowerThrottlingEnabled(IntPtr processHandle)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        if (!GetProcessInformation(processHandle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            return false;
        }
        return (state.ControlMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0
            && (state.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
    }

    /// <summary>Explicitly disables (or re-enables system-managed) power throttling for the process.</summary>
    public static void SetPowerThrottling(IntPtr processHandle, bool? enabled)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            // null → clear the control bit entirely: back to system-managed behavior.
            ControlMask = enabled is null ? 0 : PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled == true ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };

        if (!SetProcessInformation(processHandle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetProcessInformation(PowerThrottling) failed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static (ulong TotalBytes, ulong AvailableBytes) GetMemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return (status.ullTotalPhys, status.ullAvailPhys);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
