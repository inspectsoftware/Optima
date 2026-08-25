using System.Windows;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.NewValue is SetupWizardViewModel viewModel)
            {
                viewModel.Completed += (_, _) => Close();
            }
        };
    }
}
