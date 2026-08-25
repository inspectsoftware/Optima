using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace COPSBootstrapper.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();
    }

    /// <summary>Documented DWM attribute: renders the standard title bar in dark colors.</summary>
    private void EnableDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
