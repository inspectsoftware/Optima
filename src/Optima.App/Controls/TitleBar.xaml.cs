using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>Caption row used by the shell and the secondary windows.</summary>
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

    public void SyncMaximizeGlyph()
    {
        var maximized = Host?.WindowState == WindowState.Maximized;
        MaximizeGlyph.Symbol = TryFindResource(maximized ? "Icon.Restore" : "Icon.Maximize") as Geometry;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }
}
