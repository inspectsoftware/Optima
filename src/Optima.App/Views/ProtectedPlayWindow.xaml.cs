using System.Windows;

namespace Optima.App.Views;

/// <summary>
/// "What protected play does": shown before a PC is linked, and once more whenever a newer Optima
/// looks at more than the version the player last read. Optima Shield is not started on a PC whose
/// player has not seen the current text.
/// </summary>
public partial class ProtectedPlayWindow : Window
{
    public ProtectedPlayWindow() => InitializeComponent();

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    // The button works once the text has been scrolled to its end: the last part says who sees what.
    private void OnScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        => AcceptButton.IsEnabled |= e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 4;

    /// <summary>Shows the screen over the main window. True when the player pressed "I have read this".</summary>
    public static bool Ask()
        => new ProtectedPlayWindow { Owner = Application.Current?.MainWindow }.ShowDialog() == true;
}
