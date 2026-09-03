using System.Runtime.InteropServices;
using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;

namespace Optima.Platform.Windows.Services;

/// <summary>Finds the monitor showing the game window (matched by the configured title pattern) and returns its work area in device pixels.</summary>
public sealed class WindowsGameWindowLocator : IGameWindowLocator
{
    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;

    public WindowsGameWindowLocator(Func<CancellationToken, Task<DetectionRules>> rulesProvider)
    {
        _rulesProvider = rulesProvider;
    }

    public async Task<OverlayRect?> GetGameMonitorWorkAreaAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        return await Task.Run<OverlayRect?>(() =>
        {
            var gameWindow = WindowNative.GetVisibleWindows()
                .FirstOrDefault(w => w.Title.Contains(rules.GameWindowTitlePattern, StringComparison.OrdinalIgnoreCase));
            if (gameWindow is null)
            {
                return null;
            }

            var monitor = MonitorFromWindow(gameWindow.Handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return null;
            }
            return new OverlayRect(
                info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top);
        }, ct).ConfigureAwait(false);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}
