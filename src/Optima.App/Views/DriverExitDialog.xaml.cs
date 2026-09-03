using System.Windows;

namespace Optima.App.Views;

/// <summary>What the user decided about the virtual display driver on the way out.</summary>
public enum DriverExitChoice
{
    Cancel,

    Keep,

    Uninstall,
}

/// <summary>Shown when Optima is about to quit while the virtual display driver is still installed.</summary>
public partial class DriverExitDialog : Window
{
    public DriverExitChoice Choice { get; private set; } = DriverExitChoice.Cancel;

    public DriverExitDialog()
    {
        InitializeComponent();
    }

    public static DriverExitChoice Ask(Window mainWindow)
    {
        var dialog = new DriverExitDialog();
        if (mainWindow.IsVisible && mainWindow.WindowState != WindowState.Minimized)
        {
            dialog.Owner = mainWindow;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
        }
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Choice = DriverExitChoice.Keep;
        DialogResult = true;
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        Choice = DriverExitChoice.Uninstall;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = DriverExitChoice.Cancel;
        DialogResult = false;
    }
}
