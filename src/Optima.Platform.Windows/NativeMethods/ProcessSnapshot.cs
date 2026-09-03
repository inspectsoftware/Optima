using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>
/// The cheap process list every poll loop uses: one Toolhelp snapshot, pids and image names
/// only. <see cref="Process.GetProcesses"/> looks the same from the outside but parses every
/// thread of every process into managed objects on each call, roughly a megabyte of garbage
/// per scan, which is too much for something that runs several times a second while a game
/// is on screen. Names come back without the ".exe" so they match <see cref="Process.ProcessName"/>.
/// </summary>
public static class ProcessSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Every running process as (pid, name without extension).</summary>
    public static IReadOnlyList<(int Id, string Name)> GetRunning()
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue || snapshot == IntPtr.Zero)
        {
            return GetRunningSlow();
        }

        try
        {
            var result = new List<(int, string)>(256);
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return GetRunningSlow();
            }
            do
            {
                result.Add(((int)entry.th32ProcessID, StripExtension(entry.szExeFile)));
            }
            while (Process32NextW(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>The managed enumerator, kept only as the fallback when the snapshot API fails.</summary>
    private static IReadOnlyList<(int Id, string Name)> GetRunningSlow()
    {
        var result = new List<(int, string)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                result.Add((process.Id, process.ProcessName));
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }

    internal static string StripExtension(string imageName)
        => imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
}
