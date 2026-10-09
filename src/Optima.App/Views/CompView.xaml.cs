using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Optima.App.ViewModels;

namespace Optima.App.Views;

public partial class CompView
{
    private readonly DispatcherTimer _mouseReadoutTimer;

    public CompView()
    {
        InitializeComponent();
        _mouseReadoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mouseReadoutTimer.Tick += (_, _) => (DataContext as CompViewModel)?.RefreshMouseReadout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _mouseReadoutTimer.Start();
        if (DataContext is CompViewModel vm)
        {
            vm.SetDisplayScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _mouseReadoutTimer.Stop();
        // Nobody reads the meter off this page, and every mouse report it counts is handled on the
        // UI thread. Through the command, so the button reads "start meter" again.
        if (DataContext is CompViewModel { MouseMeterActive: true } vm)
        {
            vm.ToggleMouseMeterCommand.Execute(null);
        }
    }

    private void OnTestKeyDown(object sender, KeyEventArgs e)
        => (DataContext as CompViewModel)?.OnTestKeyDown();
}
