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

    /// <summary>
    /// How long one enumeration answers for. Enumerating is the expensive half of a presence poll
    /// (a GetWindowText call for every visible window on the desktop), and the callers in the same
    /// second all ask the same question.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMilliseconds(400);

    private static readonly object CacheGate = new();
    private static IReadOnlyList<TopLevelWindow>? _cached;
    private static DateTimeOffset _cachedAt;

    internal static IReadOnlyList<TopLevelWindow> GetVisibleWindows()
    {
        var now = DateTimeOffset.UtcNow;
        lock (CacheGate)
        {
            if (_cached is not null && now - _cachedAt < CacheLifetime)
            {
                return _cached;
            }

            _cached = CaptureVisibleWindows();
            _cachedAt = now;
            return _cached;
        }
    }

    private static IReadOnlyList<TopLevelWindow> CaptureVisibleWindows()
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
                // Optima's own windows are never the game, and its setup guide carries the game's
                // name in its title: counted, it kept a finished session waiting for an exit.
                if ((int)pid != Environment.ProcessId)
                {
                    windows.Add(new TopLevelWindow(hWnd, (int)pid, buffer.ToString()));
                }
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }
}
