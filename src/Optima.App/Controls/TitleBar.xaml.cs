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

    public static readonly DependencyProperty AccountsProperty = DependencyProperty.Register(
        nameof(Accounts), typeof(System.Collections.IEnumerable), typeof(TitleBar), new PropertyMetadata(null));

    public static readonly DependencyProperty ActiveAccountProperty = DependencyProperty.Register(
        nameof(ActiveAccount), typeof(object), typeof(TitleBar),
        new PropertyMetadata(null, OnActiveAccountChanged));

    public static readonly DependencyProperty HasAccountsProperty = DependencyProperty.Register(
        nameof(HasAccounts), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    /// <summary>The saved identities shown in the caption switcher.</summary>
    public System.Collections.IEnumerable? Accounts
    {
        get => (System.Collections.IEnumerable?)GetValue(AccountsProperty);
        set => SetValue(AccountsProperty, value);
    }

    /// <summary>The active identity; selecting another one requests the switch.</summary>
    public object? ActiveAccount
    {
        get => GetValue(ActiveAccountProperty);
        set => SetValue(ActiveAccountProperty, value);
    }

    public bool HasAccounts
    {
        get => (bool)GetValue(HasAccountsProperty);
        set => SetValue(HasAccountsProperty, value);
    }

    /// <summary>Raised with the newly selected account; the shell performs the switch.</summary>
    public event EventHandler<object>? AccountSwitchRequested;

    private static void OnActiveAccountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (TitleBar)d;
        bar.HasAccounts = bar.Accounts is System.Collections.IEnumerable list && list.Cast<object>().Any();
        if (e.OldValue is not null && e.NewValue is not null && !ReferenceEquals(e.OldValue, e.NewValue))
        {
            bar.AccountSwitchRequested?.Invoke(bar, e.NewValue);
        }
    }

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
