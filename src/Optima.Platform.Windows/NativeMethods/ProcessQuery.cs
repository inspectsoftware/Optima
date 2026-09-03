using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Per-process queries on a handle that is opened once and kept: start time, CPU time and working set.</summary>
public static class ProcessQuery
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    public static SafeProcessHandle? Open(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public static DateTimeOffset? GetStartTime(SafeProcessHandle process)
        => GetProcessTimes(process, out var creation, out _, out _, out _) && creation > 0
            ? DateTimeOffset.FromFileTime(creation)
            : null;

    public static TimeSpan? GetTotalProcessorTime(SafeProcessHandle process)
        => GetProcessTimes(process, out _, out _, out var kernel, out var user)
            ? TimeSpan.FromTicks(kernel + user)
            : null;

    public static ulong? GetWorkingSetBytes(SafeProcessHandle process)
    {
        var size = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();
        return GetProcessMemoryInfo(process, out var counters, size) ? counters.WorkingSetSize : null;
    }
}
