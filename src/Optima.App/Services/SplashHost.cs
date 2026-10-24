using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Optima.App.Views;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// Runs the launch splash on a UI thread of its own. Startup does all its work on the main thread,
/// and a splash sharing that thread can only animate in the gaps; on its own thread it plays every
/// frame from the first one, however busy startup is. The main thread talks to it through the
/// calls here and never touches the window directly.
/// </summary>
public sealed class SplashHost
{
    private readonly Dispatcher _dispatcher;

    // Touched on the splash thread only. Null until the window has been built there, and for good
    // when building it failed; every call below then does nothing.
    private SplashWindow? _window;

    // Set on the splash thread once the window is on screen. Other threads only ask Windows where
    // that window is, which any thread may do.
    private volatile IntPtr _handle;

    // Done once the splash thread stops, for whatever reason: a step waiting on a splash that is
    // gone must not wait for ever.
    private readonly TaskCompletionSource _gone = new();

    private SplashHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        dispatcher.ShutdownStarted += (_, _) => _gone.TrySetResult();
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
            _handle = new WindowInteropHelper(window).Handle;
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

    /// <summary>
    /// Grows the splash over the main window's bounds, once the intro has played, its ground turning to
    /// the window's. The task completes when they are covered, or at once when the splash is gone;
    /// either way the window can then be shown.
    /// </summary>
    public Task GrowIntoAsync(Rect target, Color windowGround) => Run(window => window.GrowIntoAsync(target, windowGround));

    /// <summary>
    /// Puts the main window on the splash's monitor and keeps it there, before it is shown and before
    /// its place is handed to <see cref="GrowIntoAsync"/>. A CenterScreen window is centred on the
    /// monitor under the cursor when its handle is made, a second after the splash opened, and again
    /// when it is shown: a cursor moved to another monitor in between took the window away from the
    /// card. The card then grew across two monitors, and with their DPI different it ended beside the
    /// window, because each window's Left and Top are in units of its own DPI. Centred there in the
    /// work area, as CenterScreen does.
    /// </summary>
    public void KeepOnSplashMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var splash = _handle;
        if (splash == IntPtr.Zero)
        {
            return;
        }
        var monitor = MonitorFromWindow(splash, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == MonitorFromWindow(handle, MonitorDefaultToNearest) || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }
        // Moved only, never sized: on the other monitor Windows gives the window that monitor's DPI,
        // and WPF sizes it for that DPI itself. A size passed in here was scaled a second time.
        var work = info.rcWork;
        SetWindowPos(handle, IntPtr.Zero, work.Left, work.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        if (!GetWindowRect(handle, out var bounds))
        {
            return;
        }
        SetWindowPos(handle, IntPtr.Zero,
            work.Left + (work.Right - work.Left - (bounds.Right - bounds.Left)) / 2,
            work.Top + (work.Bottom - work.Top - (bounds.Bottom - bounds.Top)) / 2,
            0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Fades the splash off the main window and closes it. The task completes when it is gone.</summary>
    public Task RevealAsync() => Run(window => window.RevealAsync());

    /// <summary>Takes the splash down at once, for a startup that failed.</summary>
    public void Close() => Post(() => _window?.CloseNow());

    private Task Run(Func<SplashWindow, Task> step)
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }
        var done = _dispatcher.InvokeAsync(() => _window is { } window ? step(window) : Task.CompletedTask).Task.Unwrap();
        return Task.WhenAny(done, _gone.Task);
    }

    private void Post(Action action)
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
