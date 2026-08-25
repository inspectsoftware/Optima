using System.Runtime.InteropServices;
using System.Text;

namespace Optima.Platform.Windows.NativeMethods;

/// <summary>Top-level window enumeration used to spot the game window by title (§4/§9).</summary>
internal static class WindowNative
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    internal sealed record TopLevelWindow(IntPtr Handle, int ProcessId, string Title);

    internal static IReadOnlyList<TopLevelWindow> GetVisibleWindows()
    {
        var windows = new List<TopLevelWindow>();
        var buffer = new StringBuilder(512);

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            buffer.Clear();
            if (GetWindowText(hWnd, buffer, buffer.Capacity) > 0)
            {
                _ = GetWindowThreadProcessId(hWnd, out var pid);
                windows.Add(new TopLevelWindow(hWnd, (int)pid, buffer.ToString()));
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }
}
