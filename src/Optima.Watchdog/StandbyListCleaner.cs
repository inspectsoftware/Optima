using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Optima.Core.Boost;
using Optima.Core.Ipc;

namespace Optima.Watchdog;

/// <summary>
/// Watches the Windows free and standby memory lists and purges the standby list when free memory is
/// short and the standby list is what holds it (what ISLC does). Lives in the elevated helper because
/// both the readout and the purge need SeProfileSingleProcessPrivilege. It works on the system's own
/// cache lists only: no process, the game least of all, has its working set touched.
/// </summary>
public sealed class StandbyListCleaner : IDisposable
{
    private readonly Func<IpcEvent, Task> _publishEvent;
    private readonly object _gate = new();
    private readonly System.Timers.Timer _timer;
    private int _freeBelowMb;
    private int _standbyAboveMb;
    private int _purges;
    private long _lastFreedMb;
    private DateTimeOffset? _lastPurgeAt;

    public StandbyListCleaner(Func<IpcEvent, Task> publishEvent, int freeBelowMb, int standbyAboveMb, int intervalMs)
    {
        _publishEvent = publishEvent;
        _freeBelowMb = freeBelowMb;
        _standbyAboveMb = standbyAboveMb;
        MemoryLists.EnablePrivilege();
        // Fails here, before the timer exists, when the lists cannot be read at all.
        MemoryLists.Query();
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Tick();
        _timer.Start();
        HelperLog.Write($"standby cleaner started (free < {freeBelowMb} MB and standby > {standbyAboveMb} MB, every {intervalMs} ms)");
        Tick();
    }

    /// <summary>New thresholds for a cleaner that is already running.</summary>
    public void Configure(int freeBelowMb, int standbyAboveMb, int intervalMs)
    {
        lock (_gate)
        {
            _freeBelowMb = freeBelowMb;
            _standbyAboveMb = standbyAboveMb;
        }
        _timer.Interval = intervalMs;
    }

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                var (freeMb, standbyMb) = MemoryLists.Query();
                if (StandbyCleanerPolicy.ShouldPurge(freeMb, standbyMb, _freeBelowMb, _standbyAboveMb))
                {
                    MemoryLists.PurgeStandby();
                    var after = MemoryLists.Query();
                    _purges++;
                    _lastFreedMb = Math.Max(0, standbyMb - after.StandbyMb);
                    _lastPurgeAt = DateTimeOffset.Now;
                    HelperLog.Write($"standby list purged: free {freeMb} -> {after.FreeMb} MB, standby {standbyMb} -> {after.StandbyMb} MB");
                    (freeMb, standbyMb) = after;
                }
                _ = _publishEvent(new IpcEvent { Kind = "memoryStatus", Data = Describe(freeMb, standbyMb, _purges, _lastFreedMb, _lastPurgeAt) });
            }
        }
        catch (Exception ex)
        {
            HelperLog.Write("standby cleaner pass failed: " + ex.Message);
        }
    }

    /// <summary>One purge on request, whatever the thresholds say. Returns the lists before and after.</summary>
    public static Dictionary<string, string> PurgeOnce()
    {
        MemoryLists.EnablePrivilege();
        var before = MemoryLists.Query();
        MemoryLists.PurgeStandby();
        var after = MemoryLists.Query();
        HelperLog.Write($"standby list purged on request: standby {before.StandbyMb} -> {after.StandbyMb} MB");
        return new Dictionary<string, string>
        {
            ["freeMb"] = after.FreeMb.ToString(CultureInfo.InvariantCulture),
            ["standbyMb"] = after.StandbyMb.ToString(CultureInfo.InvariantCulture),
            ["freedMb"] = Math.Max(0, before.StandbyMb - after.StandbyMb).ToString(CultureInfo.InvariantCulture),
        };
    }

    private static Dictionary<string, string> Describe(long freeMb, long standbyMb, int purges, long lastFreedMb, DateTimeOffset? lastPurgeAt)
        => new()
        {
            ["freeMb"] = freeMb.ToString(CultureInfo.InvariantCulture),
            ["standbyMb"] = standbyMb.ToString(CultureInfo.InvariantCulture),
            ["purges"] = purges.ToString(CultureInfo.InvariantCulture),
            ["lastFreedMb"] = lastFreedMb.ToString(CultureInfo.InvariantCulture),
            ["lastPurgeAt"] = lastPurgeAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        };

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        HelperLog.Write("standby cleaner stopped");
    }

    /// <summary>The memory-list system calls. Windows offers no documented API for either; these are the ones RAMMap and ISLC use.</summary>
    private static class MemoryLists
    {
        private const int SystemMemoryListInformation = 80;
        private const int MemoryPurgeStandbyList = 4;
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x0002;
        private const int ErrorNotAllAssigned = 1300;

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemMemoryListInfo
        {
            public nuint ZeroPageCount;
            public nuint FreePageCount;
            public nuint ModifiedPageCount;
            public nuint ModifiedNoWritePageCount;
            public nuint BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public nuint[] PageCountByPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public nuint[] RepurposedPagesByPriority;
            public nuint ModifiedPageCountPageFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr token, bool disableAll, ref TokenPrivileges newState, int length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        internal static void EnablePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out var luid))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                // Succeeds even when the token does not hold the privilege; the last error tells.
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
                    || Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                {
                    throw new InvalidOperationException("This account cannot hold the privilege the memory lists need.");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        internal static (long FreeMb, long StandbyMb) Query()
        {
            var size = Marshal.SizeOf<SystemMemoryListInfo>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemMemoryListInformation, buffer, size, out _);
                if (status != 0)
                {
                    throw new InvalidOperationException($"Reading the memory lists failed (0x{status:X8}).");
                }
                var info = Marshal.PtrToStructure<SystemMemoryListInfo>(buffer);
                var pageMb = Environment.SystemPageSize / (1024.0 * 1024.0);
                ulong standbyPages = 0;
                foreach (var count in info.PageCountByPriority)
                {
                    standbyPages += count;
                }
                var freePages = (ulong)info.ZeroPageCount + info.FreePageCount;
                return ((long)(freePages * pageMb), (long)(standbyPages * pageMb));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void PurgeStandby()
        {
            var command = MemoryPurgeStandbyList;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            if (status != 0)
            {
                throw new InvalidOperationException($"Purging the standby list failed (0x{status:X8}).");
            }
        }
    }
}
