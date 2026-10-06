using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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

    /// <summary>
    /// One view per page view-model, kept for the life of the window. The view-models are
    /// singletons, so a page that has been visited once keeps its visual tree (and its scroll
    /// position and focus) instead of being rebuilt from a DataTemplate on every rail click.
    /// </summary>
    private readonly Dictionary<object, System.Windows.Controls.UserControl> _pageViews = [];

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
                ShowPage(vm.CurrentPage);
            }
        };
    }

    public AmbientField AmbientLayer => Ambient;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
        {
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.RailCollapsed))
        {
            ApplyRail(vm.RailCollapsed);
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            ShowPage(vm.CurrentPage);
        }
    }

    private void ShowPage(object page)
    {
        if (!_pageViews.TryGetValue(page, out var view))
        {
            view = CreatePageView(page);
            if (view is null)
            {
                return;
            }

            // A DataTemplate used to hand each page its view-model as DataContext; a view instance
            // hosted directly by the ContentControl would inherit the window's instead, so the
            // page binds to the wrong object and renders empty.
            view.DataContext = page;
            _pageViews[page] = view;
        }

        if (ReferenceEquals(PageHost.Content, view))
        {
            return;
        }
        PageHost.Content = view;
        AnimatePageIn();
    }

    private static System.Windows.Controls.UserControl? CreatePageView(object page) => page switch
    {
        HomeViewModel => new HomeView(),
        PlayViewModel => new PlayView(),
        PerformanceViewModel => new PerformanceView(),
        SessionsViewModel => new SessionsView(),
        DisplayViewModel => new DisplayView(),
        CompViewModel => new CompView(),
        BoostViewModel => new BoostView(),
        LegalViewModel => new LegalView(),
        DiagnosticsViewModel => new DiagnosticsView(),
        LogsViewModel => new LogsView(),
        SettingsViewModel => new SettingsView(),
        DeveloperViewModel => new DeveloperView(),
        NewsViewModel => new NewsView(),
        UpdateLogViewModel => new UpdateLogView(),
        _ => null,
    };

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

    private void OnAccountSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main
            && e.AddedItems.Count > 0
            && e.AddedItems[0] is Optima.Core.Models.PlayerAccount account)
        {
            _ = main.SwitchAccountCommand.ExecuteAsync(account);
        }
    }

    private void AnimatePageIn()
    {
        if (!Motion.Enabled)
        {
            PageHost.Opacity = 1;
            PageShift.Y = 0;
            return;
        }

        // Short and cheap: the fade used to run for 220 ms over the whole page area, which kept the
        // compositor busy for a fifth of a second after every rail click.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration) { EasingFunction = ease });
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
