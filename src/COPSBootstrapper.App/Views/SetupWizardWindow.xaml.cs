using System.Windows;
using COPSBootstrapper.App.ViewModels;

namespace COPSBootstrapper.App.Views;

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
