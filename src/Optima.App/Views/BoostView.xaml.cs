using System.Windows;
using System.Windows.Controls;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class BoostView : UserControl
{
    /// <summary>Below this the dial and the six numbers no longer fit side by side.</summary>
    private const double SideBySideWidth = 940;

    public BoostView()
    {
        InitializeComponent();
        // The live readout runs only while the page is on screen: another page, the tray and a
        // closed window all make it invisible.
        IsVisibleChanged += (_, e) => (DataContext as BoostViewModel)?.SetLive((bool)e.NewValue);
        Hero.SizeChanged += (_, e) => ArrangeHero(e.NewSize.Width < SideBySideWidth);
    }

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
