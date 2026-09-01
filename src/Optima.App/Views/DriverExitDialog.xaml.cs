using System.Windows;

namespace Optima.App.Views;

/// <summary>What the user decided about the virtual display driver on the way out.</summary>
public enum DriverExitChoice
{
    /// <summary>Stay in Optima; nothing changes.</summary>
    Cancel,

    /// <summary>Quit and leave the driver installed.</summary>
    Keep,

    /// <summary>Remove the driver, then quit.</summary>
    Uninstall,
}

/// <summary>
/// Shown when Optima is about to quit while the virtual display driver is still installed.
/// "Keep driver" is the default (Enter) and "Cancel" the escape route, so a reflexive
/// keypress can never remove the driver.
/// </summary>
public partial class DriverExitDialog : Window
{
    public DriverExitChoice Choice { get; private set; } = DriverExitChoice.Cancel;

    public DriverExitDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Runs the dialog modally. It is owned by the main window when that window is on screen;
    /// otherwise (EXIT from the tray while hidden, minimized or started with --tray) it centers
    /// on the screen and stays on top, so it cannot get lost behind a game.
    /// </summary>
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
