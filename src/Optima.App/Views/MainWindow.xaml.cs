using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Optima.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
        {
            ApplyMaximizedCompensation();
            Caption.SyncMaximizeGlyph();
        };
    }

    /// <summary>
    /// A WindowChrome window sized to Maximized extends past the work area on every edge,
    /// which clips the caption row and the right column. The overhang is measured against
    /// the monitor the window is actually on rather than assumed from SystemParameters, which
    /// keeps it correct across monitors with different DPI.
    /// </summary>
    private void ApplyMaximizedCompensation()
    {
        if (WindowState != WindowState.Maximized)
        {
            RootBorder.Padding = new Thickness(0);
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero
            || !GetWindowRect(handle, out var window)
            || !TryGetWorkArea(handle, out var work))
        {
            RootBorder.Padding = new Thickness(0);
            return;
        }

        // Physical pixels → DIPs, since Padding is expressed in DIPs.
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (scaleX <= 0 || scaleY <= 0)
        {
            scaleX = scaleY = 1.0;
        }

        RootBorder.Padding = new Thickness(
            Math.Max(0, work.Left - window.Left) / scaleX,
            Math.Max(0, work.Top - window.Top) / scaleY,
            Math.Max(0, window.Right - work.Right) / scaleX,
            Math.Max(0, window.Bottom - work.Bottom) / scaleY);
    }

    private static bool TryGetWorkArea(IntPtr handle, out RECT workArea)
    {
        workArea = default;
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }
        workArea = info.rcWork;
        return true;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
