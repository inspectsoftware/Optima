using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Optima.App.Controls;
using Optima.App.Services;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class MainWindow : Window
{
    private const double RailWidth = 200;
    private const double RailCollapsedWidth = 56;

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
        Closed += (_, _) =>
        {
            ThemeService.ThemeApplied -= ApplyWindowDressing;
            Motion.Changed -= OnMotionChanged;
        };

        MouseMove += OnPointerMoved;
        MouseLeave += (_, _) => GlassPanel.ClearLights();
        Activated += (_, _) => Motion.SetForeground(true);
        Deactivated += (_, _) => Motion.SetForeground(false);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                Motion.SetForeground(false);
            }
        };
        Motion.Changed += OnMotionChanged;

        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnViewModelPropertyChanged;
            }
            if (args.NewValue is INotifyPropertyChanged next)
            {
                next.PropertyChanged += OnViewModelPropertyChanged;
            }
            if (args.NewValue is MainViewModel vm)
            {
                ApplyRail(vm.RailCollapsed);
            }
        };
    }

    public AmbientField AmbientLayer => Ambient;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RailCollapsed) && sender is MainViewModel vm)
        {
            ApplyRail(vm.RailCollapsed);
        }
    }

    private void ApplyRail(bool collapsed)
    {
        RailColumn.Width = new GridLength(collapsed ? RailCollapsedWidth : RailWidth);
    }

    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        if (Motion.Enabled)
        {
            GlassPanel.NotifyPointer(this, e.GetPosition(this));
        }
    }

    private void OnMotionChanged()
    {
        if (!Motion.Enabled)
        {
            Dispatcher.BeginInvoke(GlassPanel.ClearLights);
        }
    }

    private void OnPageChanged(object sender, DataTransferEventArgs e)
    {
        if (!Motion.Enabled)
        {
            PageHost.Opacity = 1;
            PageShift.Y = 0;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

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

        Ambient.Accent = ThemeService.CurrentAccent;

        var dark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var backdrop = DWMSBT_TRANSIENTWINDOW;
        _backdropActive = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

        RootBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            _backdropActive ? "Brush.WindowGround" : "Brush.Background");
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
