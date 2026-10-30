using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optima.Core.Abstractions;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>
/// A process's GPU scheduling priority class, through the documented D3DKMT calls in gdi32. Both
/// need the process opened for PROCESS_SET_INFORMATION, reading included; for a process of the same
/// user that needs no administrator rights, and neither does raising it to high.
/// </summary>
public static class GpuPriorityNative
{
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int STATUS_SUCCESS = 0;
    private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    private const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTGetProcessSchedulingPriorityClass(SafeProcessHandle process, out int priority);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(SafeProcessHandle process, int priority);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    public static GpuPriorityResult Read(int processId, out int priorityClass)
    {
        priorityClass = 0;
        using var process = OpenProcess(PROCESS_SET_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return OpenFailure(Marshal.GetLastWin32Error());
        }
        return ResultOf(D3DKMTGetProcessSchedulingPriorityClass(process, out priorityClass));
    }

    public static GpuPriorityResult Set(int processId, int priorityClass)
    {
        using var process = OpenProcess(PROCESS_SET_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return OpenFailure(Marshal.GetLastWin32Error());
        }
        return ResultOf(D3DKMTSetProcessSchedulingPriorityClass(process, priorityClass));
    }

    /// <summary>A process that cannot be opened is refused when access was denied, and gone otherwise.</summary>
    public static GpuPriorityResult OpenFailure(int win32Error)
        => win32Error == ERROR_ACCESS_DENIED ? GpuPriorityResult.Refused : GpuPriorityResult.Unavailable;

    /// <summary>
    /// What an NTSTATUS from the D3DKMT calls means here. Any other failure is taken for a process
    /// the graphics kernel does not know: one that has not used the graphics card answers
    /// STATUS_INVALID_PARAMETER.
    /// </summary>
    public static GpuPriorityResult ResultOf(int status) => status switch
    {
        STATUS_SUCCESS => GpuPriorityResult.Done,
        STATUS_ACCESS_DENIED or STATUS_PRIVILEGE_NOT_HELD => GpuPriorityResult.Refused,
        _ => GpuPriorityResult.Unavailable,
    };
}
