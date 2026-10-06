using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Optima.App.Views;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// Runs the launch splash on a UI thread of its own. Startup does all its work on the main thread,
/// and a splash sharing that thread can only animate in the gaps; on its own thread it plays every
/// frame from the first one, however busy startup is. The main thread talks to it through the
/// three calls here and never touches the window directly.
/// </summary>
public sealed class SplashHost
{
    private readonly Dispatcher _dispatcher;
    private readonly SplashWindow _window;

    private SplashHost(Dispatcher dispatcher, SplashWindow window)
    {
        _dispatcher = dispatcher;
        _window = window;
    }

    /// <summary>Shows the splash and returns once it is on screen. Null when it could not be started; startup goes on without it.</summary>
    public static SplashHost? Show(bool animate, string version)
    {
        SplashHost? host = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var window = new SplashWindow(animate, version);
                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();
                host = new SplashHost(Dispatcher.CurrentDispatcher, window);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The splash could not be shown");
            }
            finally
            {
                ready.Set();
            }
            if (host is not null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "Optima splash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return host;
    }

    /// <summary>Names the startup stage under the mark; <paramref name="fraction"/> fills the line beneath it.</summary>
    public void SetStatus(string text, double fraction) => Post(() => _window.SetStatus(text, fraction));

    public void SetAccent(Color accent) => Post(() => _window.SetAccent(accent));

    /// <summary>Opens the splash into the main window's bounds and closes it. Safe to call once the main window is shown.</summary>
    public Task OpenIntoAsync(Rect target)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(() => _window.OpenIntoAsync(target)).Task.Unwrap();
    }

    /// <summary>Takes the splash down at once, for a startup that failed.</summary>
    public void Close() => Post(_window.CloseNow);

    private void Post(Action action)
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
