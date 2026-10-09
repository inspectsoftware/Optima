using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Optima.Core.Theming;
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
    private readonly Dictionary<object, UserControl> _pageViews = [];

    private bool _backdropActive;

    public MainWindow()
    {
        InitializeComponent();
        // Never opened larger than the screen it opens on: centered, that put the title bar above the top edge.
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height));
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width));
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
        // The app's, not this window's: with one of Optima's own dialogs in front the app is still
        // the one being used, and the dialog's backdrop and controls move like everything else.
        Application.Current.Activated += (_, _) => Motion.SetForeground(IsVisible);
        Application.Current.Deactivated += (_, _) => Motion.SetForeground(false);
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
                ApplyRail(vm.RailCollapsed, glide: false);
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
            ApplyRail(vm.RailCollapsed, glide: true);
            // The rows change height and the section headings go; the marker follows once that is laid out.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => PlaceNavMarker(glide: false));
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

        // Laid out now, not on the next frame, so that the page's sections exist to be brought in
        // and are never shown standing still first.
        PageHost.UpdateLayout();
        var order = 0;
        foreach (var part in Parts(view))
        {
            Motion.Rise(part, order++);
        }
        PlaceNavMarker(glide: true);
        Ambient.Pour(page.GetType().Name);
    }

    /// <summary>
    /// What arrives one after another when a page opens: the sections in the page's own column,
    /// which is the first panel down from the view that holds more than one thing.
    /// </summary>
    private static IEnumerable<UIElement> Parts(UserControl view)
    {
        object? node = view.Content;
        for (var depth = 0; depth < 6; depth++)
        {
            switch (node)
            {
                case Panel { Children.Count: > 1 } column:
                    return column.Children.Cast<UIElement>().Where(child => child.Visibility == Visibility.Visible).ToList();
                case Panel { Children.Count: 1 } single:
                    node = single.Children[0];
                    break;
                case Decorator decorator:
                    node = decorator.Child;
                    break;
                case ContentControl content:
                    node = content.Content;
                    break;
                default:
                    return [view];
            }
        }
        return [view];
    }

    private double _navMarkerTop = double.NaN;

    private void OnNavListLoaded(object sender, RoutedEventArgs e) => PlaceNavMarker(glide: false);

    /// <summary>Sends the rail's marker to the row of the open page.</summary>
    private void PlaceNavMarker(bool glide)
    {
        var active = (DataContext as MainViewModel)?.NavItems.FirstOrDefault(item => item.IsActive);
        var row = active is null ? null : FirstButton(NavList.ItemContainerGenerator.ContainerFromItem(active));
        if (row is null || !row.IsVisible || row.ActualHeight <= 0)
        {
            NavMarker.Opacity = 0;
            _navMarkerTop = double.NaN;
            return;
        }

        // The row's template keeps a pixel clear above and below; so does the marker.
        var top = row.TransformToAncestor(NavArea).Transform(new Point(0, 0)).Y + 1;
        NavMarker.Height = Math.Max(0, row.ActualHeight - 2);
        if (top == _navMarkerTop)
        {
            return;
        }
        var seconds = glide && !double.IsNaN(_navMarkerTop) ? Motion.Seconds(MotionSpec.MoveMs) : 0;
        _navMarkerTop = top;
        NavMarker.Opacity = 1;
        NavMarkerShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(top, TimeSpan.FromSeconds(seconds)) { EasingFunction = Motion.Ease });
    }

    private static Button? FirstButton(DependencyObject? root)
    {
        if (root is null or Button)
        {
            return root as Button;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FirstButton(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static UserControl? CreatePageView(object page) => page switch
    {
        HomeViewModel => new HomeView(),
        PlayViewModel => new PlayView(),
        PerformanceViewModel => new PerformanceView(),
        SessionsViewModel => new SessionsView(),
        DisplayViewModel => new DisplayView(),
        CompViewModel => new CompView(),
        BoostViewModel => new BoostView(),
        LegalViewModel => new LegalView(),
        DebugViewModel => new DebugView(),
        SettingsViewModel => new SettingsView(),
        DeveloperViewModel => new DeveloperView(),
        NewsViewModel => new NewsView(),
        UpdateLogViewModel => new UpdateLogView(),
        _ => null,
    };

    private void ApplyRail(bool collapsed, bool glide)
    {
        Rail.BeginAnimation(WidthProperty,
            new DoubleAnimation(collapsed ? RailCollapsedWidth : RailWidth, TimeSpan.FromSeconds(glide ? Motion.Seconds(MotionSpec.MoveMs) : 0))
            {
                EasingFunction = Motion.Ease,
            });
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

    private void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main
            && e.AddedItems.Count > 0
            && e.AddedItems[0] is Optima.Core.Models.PlayerAccount account)
        {
            _ = main.SwitchAccountCommand.ExecuteAsync(account);
        }
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

        RootBorder.SetResourceReference(Border.BackgroundProperty,
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
