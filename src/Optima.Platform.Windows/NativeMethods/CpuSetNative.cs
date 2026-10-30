using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optima.Core.Abstractions;
using Optima.Core.Boost;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>
/// Documented CPU set APIs: the system's CPU set table, the size of each last-level cache, and a
/// process's default CPU sets. None of them needs administrator rights for a process of the same user.
/// </summary>
public static class CpuSetNative
{
    private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;
    private const int RelationCache = 2;

    // Flag bits of SYSTEM_CPU_SET_INFORMATION.CpuSet.AllFlags.
    private const byte Allocated = 0x2;
    private const byte RealTime = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    /// <summary>Every logical processor in the CPU set table, with the size of the last-level cache it shares.</summary>
    public static IReadOnlyList<LogicalCpu> ReadProcessors()
    {
        var l3 = ReadL3Sizes();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation failed");
        }
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation failed");
            }
            var cpus = new List<LogicalCpu>();
            for (var offset = 0; offset < length;)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                {
                    break;
                }
                // Size and Type, then the CpuSet member of the union.
                var set = buffer + offset + 8;
                var group = (ushort)Marshal.ReadInt16(set, 4);
                var logical = Marshal.ReadByte(set, 6);
                var flags = Marshal.ReadByte(set, 11);
                cpus.Add(new LogicalCpu(
                    Id: (uint)Marshal.ReadInt32(set, 0),
                    Group: group,
                    LogicalIndex: logical,
                    Core: Marshal.ReadByte(set, 7),
                    LastLevelCache: Marshal.ReadByte(set, 8),
                    EfficiencyClass: Marshal.ReadByte(set, 10),
                    SchedulingClass: Marshal.ReadByte(set, 12),
                    Reserved: (flags & (Allocated | RealTime)) != 0,
                    L3Bytes: l3.GetValueOrDefault((group, logical))));
                offset += size;
            }
            return cpus;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The level 3 cache size each logical processor sees, by processor group and number.</summary>
    private static Dictionary<(ushort, byte), long> ReadL3Sizes()
    {
        var sizes = new Dictionary<(ushort, byte), long>();
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return sizes;
        }
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length))
            {
                return sizes;
            }
            for (var offset = 0; offset < length;)
            {
                var entry = buffer + offset;
                var size = Marshal.ReadInt32(entry, 4);
                if (size <= 0)
                {
                    break;
                }
                // CACHE_RELATIONSHIP after Relationship and Size: Level at 0, CacheSize at 4,
                // GroupCount at 30 (0 before Windows 11, meaning one), GROUP_AFFINITY masks from 32.
                var cache = entry + 8;
                if (Marshal.ReadByte(cache, 0) == 3)
                {
                    var bytes = (uint)Marshal.ReadInt32(cache, 4);
                    var masks = Math.Max(1, (int)(ushort)Marshal.ReadInt16(cache, 30));
                    for (var m = 0; m < masks; m++)
                    {
                        var affinity = (ulong)Marshal.ReadInt64(cache, 32 + 16 * m);
                        var group = (ushort)Marshal.ReadInt16(cache, 32 + 16 * m + 8);
                        for (byte bit = 0; bit < 64; bit++)
                        {
                            if ((affinity >> bit & 1) != 0)
                            {
                                sizes[(group, bit)] = bytes;
                            }
                        }
                    }
                }
                offset += size;
            }
            return sizes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint THREAD_SET_LIMITED_INFORMATION = 0x0400;
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    private const int ThreadBasicInformation = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(SafeProcessHandle process, uint[] cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint desiredAccess, bool inheritHandle, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadSelectedCpuSets(SafeWaitHandle thread, uint[] cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadSelectedCpuSets(SafeWaitHandle thread, uint[] cpuSetIds, uint cpuSetIdCount);

    // THREAD_BASIC_INFORMATION on x64: ExitStatus, TebBaseAddress, ClientId (two pointers), then
    // AffinityMask at 32, Priority, BasePriority. The documented way to read a thread's hard affinity
    // without changing it does not exist; this is the one every process tool uses.
    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_BASIC_INFORMATION
    {
        public int ExitStatus;
        public IntPtr TebBaseAddress;
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
        public UIntPtr AffinityMask;
        public int Priority;
        public int BasePriority;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(SafeWaitHandle thread, int informationClass, out THREAD_BASIC_INFORMATION information, int length, IntPtr returnLength);

    /// <summary>A process's default CPU sets: empty when it has none, null when it cannot be read.</summary>
    public static IReadOnlyList<uint>? GetDefaultCpuSets(int processId)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }
        var ids = new uint[256];
        return GetProcessDefaultCpuSets(process, ids, (uint)ids.Length, out var count) ? ids[..(int)count] : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(SafeWaitHandle thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessIdOfThread(SafeWaitHandle thread);

    private static int[] ThreadIdsOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return [.. process.Threads.Cast<System.Diagnostics.ProcessThread>().Select(t => t.Id)];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The threads of a process that have CPU sets of their own, with when each was created.</summary>
    public static IReadOnlyList<ThreadCpuSets> GetThreadCpuSets(int processId)
    {
        var found = new List<ThreadCpuSets>();
        var ids = new uint[256];
        foreach (var threadId in ThreadIdsOf(processId))
        {
            using var thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, threadId);
            if (!thread.IsInvalid && GetThreadTimes(thread, out var created, out _, out _, out _)
                && GetThreadSelectedCpuSets(thread, ids, (uint)ids.Length, out var count) && count > 0)
            {
                found.Add(new ThreadCpuSets(threadId, created, ids[..(int)count]));
            }
        }
        return found;
    }

    /// <summary>
    /// Gives one thread its own CPU sets, only while it is still the thread that was read: a thread
    /// of the same process created at the same moment. A thread id is reused once its thread ends.
    /// </summary>
    public static bool SetThreadCpuSets(int processId, ThreadCpuSets thread, IReadOnlyList<uint> cpuSetIds)
    {
        using var handle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION | THREAD_SET_LIMITED_INFORMATION, false, thread.ThreadId);
        if (handle.IsInvalid || GetProcessIdOfThread(handle) != processId
            || !GetThreadTimes(handle, out var created, out _, out _, out _) || created != thread.CreatedAt)
        {
            return false;
        }
        var ids = cpuSetIds.ToArray();
        return SetThreadSelectedCpuSets(handle, ids, (uint)ids.Length);
    }

    /// <summary>
    /// How many threads of a process have a hard affinity that leaves out every one of
    /// <paramref name="logicalProcessors"/>. A pin that includes some of them still runs the thread
    /// there, since Windows runs a thread on the CPU sets inside its affinity; only a pin with none
    /// of them in it wins over the CPU sets and takes the thread off the chosen cores. Counted, not
    /// changed: the program pinned it for a reason of its own (crosvm does, for its clock).
    /// </summary>
    public static int CountPinnedThreads(int processId, IReadOnlyList<int> logicalProcessors)
    {
        var planMask = logicalProcessors.Where(l => l < 64).Aggregate(0UL, (mask, l) => mask | 1UL << l);
        var pinned = 0;
        foreach (var threadId in ThreadIdsOf(processId))
        {
            using var thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, threadId);
            if (!thread.IsInvalid
                && NtQueryInformationThread(thread, ThreadBasicInformation, out var info, Marshal.SizeOf<THREAD_BASIC_INFORMATION>(), IntPtr.Zero) == 0
                && IsPinnedOff((ulong)info.AffinityMask, planMask))
            {
                pinned++;
            }
        }
        return pinned;
    }

    /// <summary>Whether a hard affinity has none of the chosen logical processors in it.</summary>
    public static bool IsPinnedOff(ulong affinity, ulong planMask) => planMask != 0 && (affinity & planMask) == 0;

    /// <summary>
    /// Sets a process's default CPU sets, or clears them when <paramref name="cpuSetIds"/> is null.
    /// They belong to the process and end with it. False when the process is gone or cannot be opened.
    /// </summary>
    public static bool SetDefaultCpuSets(int processId, IReadOnlyList<uint>? cpuSetIds)
    {
        using var process = OpenProcess(PROCESS_SET_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return false;
        }
        var ids = cpuSetIds?.ToArray();
        return SetProcessDefaultCpuSets(process, ids, (uint)(ids?.Length ?? 0));
    }
}
