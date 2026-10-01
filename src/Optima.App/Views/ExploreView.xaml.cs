using System.Windows.Input;
using Optima.App.ViewModels;

namespace Optima.App.Views;

/// <summary>
/// The EXPLORE page: leaderboards, clan lookup, and any player's profile. Clicking a clan row opens
/// the clan pane; typing an id or in-game name and pressing Enter opens that player's profile.
/// </summary>
public partial class ExploreView : System.Windows.Controls.UserControl
{
    public ExploreView()
    {
        InitializeComponent();
    }

    private void OnClanClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ExploreClanRow row }
            && DataContext is ExploreViewModel viewModel)
        {
            viewModel.OpenClanCommand.Execute(row);
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter and not Key.System
            && DataContext is ExploreViewModel viewModel)
        {
            var token = viewModel.SearchText.Trim();
            if (token.Length > 0)
            {
                viewModel.OpenProfile(token);
            }
        }
    }
}
