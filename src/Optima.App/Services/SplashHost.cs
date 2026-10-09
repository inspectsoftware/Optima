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

    // Touched on the splash thread only. Null until the window has been built there, and for good
    // when building it failed; every call below then does nothing.
    private SplashWindow? _window;

    private SplashHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Starts the splash and returns as soon as its thread can take calls, without waiting for the
    /// window to be on screen: building it takes a couple of hundred milliseconds the main thread
    /// can spend on startup. Calls made before the window exists queue up behind its creation.
    /// Null when the thread could not be started; startup goes on without a splash.
    /// </summary>
    public static SplashHost? Show(bool animate, string version)
    {
        SplashHost? host = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                // The splash is decoration. Whatever goes wrong on its thread ends the splash, not
                // the start it was decorating.
                dispatcher.UnhandledException += (_, args) =>
                {
                    Log.Warning(args.Exception, "The splash failed and was closed");
                    args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                };
                var created = new SplashHost(dispatcher);
                // Queued before anyone else can post: the dispatcher runs same-priority work in
                // order, so every later call finds the window already built (or known to be absent).
                dispatcher.BeginInvoke(() => created.Open(animate, version));
                host = created;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The splash could not be started");
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

    private void Open(bool animate, string version)
    {
        try
        {
            var window = new SplashWindow(animate, version);
            window.Closed += (_, _) => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Show();
            _window = window;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The splash could not be shown");
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    /// <summary>Names the startup stage under the mark; <paramref name="fraction"/> fills the line beneath it.</summary>
    public void SetStatus(string text, double fraction) => Post(() => _window?.SetStatus(text, fraction));

    public void SetAccent(Color accent) => Post(() => _window?.SetAccent(accent));

    /// <summary>Opens the splash into the main window's bounds and closes it. Safe to call once the main window is shown.</summary>
    public Task OpenIntoAsync(Rect target)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(() => _window?.OpenIntoAsync(target) ?? Task.CompletedTask).Task.Unwrap();
    }

    /// <summary>Takes the splash down at once, for a startup that failed.</summary>
    public void Close() => Post(() => _window?.CloseNow());

    private void Post(Action action)
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
