using System.Windows;
using System.Windows.Input;
using Optima.App.ViewModels;

namespace Optima.App.Views;

/// <summary>
/// Hosts the five-step Critical Ops on PC setup guide; clicking a rail card or the Next/Back buttons
/// moves between steps, and the window closes when the last step's action finishes the guide.
/// </summary>
public partial class PlayGuideWindow : Window
{
    public PlayGuideWindow()
    {
        InitializeComponent();
        // Fixed size: on a small or scaled screen the Back/Next row must still be on it.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        DataContextChanged += (_, args) =>
        {
            if (args.NewValue is PlayGuideViewModel viewModel)
            {
                viewModel.RequestClose += Close;
            }
            if (args.OldValue is PlayGuideViewModel oldViewModel)
            {
                oldViewModel.RequestClose -= Close;
            }
        };
        // The view model outlives the window: without this every closed guide stays referenced by it.
        Closed += (_, _) =>
        {
            if (DataContext is PlayGuideViewModel viewModel)
            {
                viewModel.RequestClose -= Close;
            }
        };
    }

    private void OnStepCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlayGuideStep step }
            && DataContext is PlayGuideViewModel viewModel)
        {
            viewModel.CurrentStepIndex = step.Index;
        }
    }
}
