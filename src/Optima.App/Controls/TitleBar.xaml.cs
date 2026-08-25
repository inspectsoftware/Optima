using System.Windows;
using System.Windows.Controls;

namespace Optima.App.Controls;

/// <summary>
/// Custom window caption used by both windows. The host supplies the breadcrumb and the
/// session tag; drag/double-click-maximize come from <see cref="System.Windows.Shell.WindowChrome"/>,
/// so native snap and resize behavior is preserved.
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty BreadcrumbProperty = DependencyProperty.Register(
        nameof(Breadcrumb), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SessionTagProperty = DependencyProperty.Register(
        nameof(SessionTag), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowMinimizeProperty = DependencyProperty.Register(
        nameof(ShowMinimize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowMaximizeProperty = DependencyProperty.Register(
        nameof(ShowMaximize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    public string Breadcrumb
    {
        get => (string)GetValue(BreadcrumbProperty);
        set => SetValue(BreadcrumbProperty, value);
    }

    public string SessionTag
    {
        get => (string)GetValue(SessionTagProperty);
        set => SetValue(SessionTagProperty, value);
    }

    public bool ShowMinimize
    {
        get => (bool)GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncMaximizeGlyph();
    }

    private Window? Host => Window.GetWindow(this);

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Host is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (Host is not { } window)
        {
            return;
        }
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        SyncMaximizeGlyph();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Host?.Close();

    /// <summary>Keeps the glyph honest when the state changes by any route (snap, double-click, Win+Up).</summary>
    public void SyncMaximizeGlyph()
    {
        if (Host is not { } window)
        {
            return;
        }
        var maximized = window.WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "▣" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }
}
