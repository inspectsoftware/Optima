using System.Windows;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
        // Fixed size, and taller than a 720p or a scaled laptop screen: the button that finishes
        // the wizard was under the taskbar there.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        DataContextChanged += (_, args) =>
        {
            if (args.NewValue is SetupWizardViewModel viewModel)
            {
                viewModel.Completed += (_, _) => Close();
            }
        };
    }
}
