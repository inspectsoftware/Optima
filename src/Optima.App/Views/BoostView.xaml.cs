using System.Windows;
using System.Windows.Controls;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class BoostView : UserControl
{
    /// <summary>Below this the dial and the six numbers no longer fit side by side.</summary>
    private const double SideBySideWidth = 940;

    private Window? _window;

    public BoostView()
    {
        InitializeComponent();
        // The live readout runs only while the page is on screen: another page, the tray and a
        // closed window all make it invisible. A minimized window stays visible to WPF, so its
        // state is followed too; otherwise the 60 s line joined two visits across the gap.
        IsVisibleChanged += (_, _) => SyncLive();
        Loaded += (_, _) =>
        {
            if (_window is null && Window.GetWindow(this) is { } window)
            {
                _window = window;
                _window.StateChanged += OnWindowStateChanged;
            }
            SyncLive();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
            {
                _window.StateChanged -= OnWindowStateChanged;
                _window = null;
            }
        };
        Hero.SizeChanged += (_, e) => ArrangeHero(e.NewSize.Width < SideBySideWidth);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => SyncLive();

    private void SyncLive()
        => (DataContext as BoostViewModel)?.SetLive(IsVisible && _window?.WindowState != WindowState.Minimized);

    /// <summary>
    /// The dial and the numbers side by side, two to a row, as the design has them; with the window
    /// at its narrowest the numbers go under the dial, three to a row.
    /// </summary>
    private void ArrangeHero(bool stacked)
    {
        Grid.SetColumnSpan(DialPanel, stacked ? 3 : 1);
        Grid.SetRow(TileGrid, stacked ? 1 : 0);
        Grid.SetColumn(TileGrid, stacked ? 0 : 2);
        Grid.SetColumnSpan(TileGrid, stacked ? 3 : 1);
        TileGrid.Margin = new Thickness(-6, stacked ? 14 : -6, -6, -6);
        TileGrid.Tag = stacked ? 3 : 2;
    }
}
