using System.ComponentModel;
using System.Windows;

namespace Optima.App.Views;

/// <summary>Floating log console toggled by the global Alt+F9 hotkey.</summary>
public partial class ConsoleWindow : Window
{
    public ConsoleWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The toggle must survive the user clicking X, so closing only hides. App shutdown is
    /// unaffected: Application.Shutdown ignores the cancellation.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
