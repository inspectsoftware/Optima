using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Optima.App.Services;

namespace Optima.App.Views;

public partial class MainWindow : Window
{
    private bool _backdropActive;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
        {
            ApplyMaximizedCompensation();
            Caption.SyncMaximizeGlyph();
        };
        SourceInitialized += (_, _) => ApplyWindowDressing(ThemeService.CurrentTheme);
        ThemeService.ThemeApplied += ApplyWindowDressing;
        Closed += (_, _) => ThemeService.ThemeApplied -= ApplyWindowDressing;
    }

    /// <summary>
    /// Asks DWM for the acrylic system backdrop and the theme-matched caption. When the
    /// backdrop is unavailable (older Windows), the translucent window ground would sit
    /// on nothing, so it falls back to the solid background brush.
    /// </summary>
    private void ApplyWindowDressing(string theme)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyWindowDressing(theme));
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var backdrop = DWMSBT_TRANSIENTWINDOW;
        _backdropActive = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

        // Translucent ground over acrylic; solid ground when there is nothing behind it.
        RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            _backdropActive ? "Brush.WindowGround" : "Brush.Background");
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
