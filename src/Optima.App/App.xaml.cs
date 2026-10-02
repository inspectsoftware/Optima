using System.Windows;
using System.Windows.Threading;
using Optima.App.Logging;
using Optima.App.Services;
using Optima.App.ViewModels;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Optima.App;

public partial class App : Application
{
    private IHost? _host;
    private GlobalHotkeys? _hotkeys;
    private TrayService? _tray;
    private AppShutdown? _shutdown;
    private ConsoleWindow? _console;
    private OverlayController? _overlay;
    private ThemeService? _theme;
    private SplashWindow? _splash;

    // One instance at a time: a second launch must never stack a second Optima, it should
    // bring the running one back instead (the exit path can stall, so users relaunch).
    private const string SingleInstanceMutexName = "Local\\Optima.SingleInstance";
    private const string SingleInstanceActivateEventName = "Local\\Optima.SingleInstance.Activate";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;

    public static LoggingLevelSwitch LogLevelSwitch { get; } = new(LogEventLevel.Information);
    public static InAppLogSink LogSink { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything else, and before the log is opened: a second instance must exit
        // quietly without touching files the first one owns.
        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown(0);
            return;
        }
        StartActivationListener();

        var paths = new AppPaths();
        paths.EnsureCreated();

        // The splash owns the screen while the host builds; skipped for a tray autostart,
        // where no window is wanted at all.
        var startInTrayArg = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTrayArg)
        {
            _splash = new SplashWindow();
            _splash.Show();
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsDirectory, "optima-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u5}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(LogSink)
            .WriteTo.Debug()
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services => AppServices.Register(services, paths))
            .Build();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _host.Start();
        Log.Information("Optima starting (version {Version})",
            typeof(App).Assembly.GetName().Version);

        var settingsService = _host.Services.GetRequiredService<SettingsService>();
        _theme = new ThemeService(settingsService);
        // Theme must be on the wall before the first window paints. The synchronous read keeps
        // startup on this thread instead of blocking it on a thread pool hop.
        var initialSettings = settingsService.GetSettings();
        _theme.Initialize(initialSettings);
        Motion.SetFollowWindows(initialSettings.FollowWindowsMotion);
        settingsService.SettingsChanged += (_, s) => Dispatcher.BeginInvoke(() => Motion.SetFollowWindows(s.FollowWindowsMotion));

        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = mainViewModel };
        MainWindow = window;
        // The hidden console window must not keep the app alive after the main window closes.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray)
        {
            window.Show();
            // The splash sits on top and expands into the window's bounds, revealing the
            // app that was already rendered underneath; no flash of empty desktop.
            _ = _splash?.ExpandIntoAsync(window, TimeSpan.FromSeconds(1.2));
        }
        else
        {
            // Never activated, so the window would otherwise count as foreground and keep
            // the ambient drift ticking on a window nobody can see.
            Motion.SetForeground(false);
        }
        if (e.Args.Any(a => string.Equals(a, "--glass-lab", StringComparison.OrdinalIgnoreCase)))
        {
            new GlassLabWindow().Show();
        }

        _hotkeys = new GlobalHotkeys(window);
        _hotkeys.ConsoleRequested += ToggleConsole;
        _hotkeys.KillGameRequested += KillGameFromHotkey;

        _overlay = new OverlayController(
            _host.Services.GetRequiredService<OverlayViewModel>(),
            _host.Services.GetRequiredService<IGameWindowLocator>(),
            _host.Services.GetRequiredService<SettingsService>(),
            _host.Services.GetRequiredService<LaunchOrchestrator>(),
            _host.Services.GetRequiredService<INetworkQualityMonitor>());
        _hotkeys.OverlayRequested += () => _overlay!.Toggle();

        _shutdown = new AppShutdown(window, _host.Services.GetRequiredService<IDriverInstaller>());

        _tray = new TrayService(window, _host.Services.GetRequiredService<SettingsService>(), _shutdown);
        var orchestrator = _host.Services.GetRequiredService<LaunchOrchestrator>();
        _tray.AttachOrchestrator(orchestrator);
        orchestrator.ProgressChanged += (_, progress) => Dispatcher.BeginInvoke(() =>
            window.AmbientLayer.State = progress.Phase switch
            {
                LaunchPhase.Failed => Controls.AmbientState.Attention,
                LaunchPhase.Idle or LaunchPhase.Completed => Controls.AmbientState.Rest,
                _ => Controls.AmbientState.Session,
            });
        _tray.TerminateGameRequested += KillGameFromHotkey;
        _tray.NavigateRequested += page =>
        {
            _tray!.ShowMainWindow();
            _ = mainViewModel.NavigateCommand.ExecuteAsync(page);
        };

        _host.Services.GetRequiredService<Optima.Core.Crashes.CrashSentinel>().Start();
        // The Watchdog's stats arm: public-profile deltas around each run (needs the
        // player's in-game name in Settings; without it, it never touches the network).
        _host.Services.GetRequiredService<Optima.Core.Stats.SessionStatsEnricher>().Start();
        var discord = _host.Services.GetRequiredService<Services.DiscordPresenceService>();
        discord.AttachLauncherWindow(window);
        _ = discord.StartAsync();

        var hardware = _host.Services.GetRequiredService<IPerformanceMonitor>();
        void SyncHardwareMonitor()
        {
            var wanted = window.IsVisible && window.WindowState != WindowState.Minimized;
            _ = RunQuietlyAsync(wanted ? hardware.StartAsync() : hardware.StopAsync(), "Hardware monitor toggle");
        }
        window.IsVisibleChanged += (_, _) => SyncHardwareMonitor();
        window.StateChanged += (_, _) => SyncHardwareMonitor();
        SyncHardwareMonitor();

        var presence = _host.Services.GetRequiredService<Optima.Core.Monitoring.GamePresenceService>();
        var crashRelaunch = _host.Services.GetRequiredService<Optima.Core.Launch.CrashAutoRelaunchService>();
        crashRelaunch.Start(presence);
        var sessionTweaks = _host.Services.GetRequiredService<Optima.Core.Launch.SessionTweakService>();
        sessionTweaks.Start();
        _ = SyncSessionTweaksAsync(sessionTweaks);
        presence.PresenceChanged += change =>
            SetOwnPriority(gameOnScreen: change.Current == Optima.Core.Monitoring.GamePresence.InGame);

        _ = mainViewModel.InitializeAsync();
    }

    private static async Task RunQuietlyAsync(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
        }
    }

    private async Task SyncSessionTweaksAsync(Core.Launch.SessionTweakService sessionTweaks)
    {
        try
        {
            var settingsService = _host!.Services.GetRequiredService<SettingsService>();
            var sync = async () =>
            {
                var s = await settingsService.GetSettingsAsync();
                var ids = new List<string>();
                if (s.SessionTweakHdrOff)
                {
                    ids.Add("session-hdr-off");
                }
                if (s.SessionTweakGameBarOff)
                {
                    ids.Add("session-gamebar-off");
                }
                if (s.SessionTweakFseOff)
                {
                    ids.Add("session-fse-off");
                }
                sessionTweaks.EnabledIds = ids;
            };
            await sync();
            settingsService.SettingsChanged += async (_, _) => await sync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Session tweak sync failed");
        }
    }

    private static void SetOwnPriority(bool gameOnScreen)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var target = gameOnScreen
                ? System.Diagnostics.ProcessPriorityClass.BelowNormal
                : System.Diagnostics.ProcessPriorityClass.Normal;
            if (self.PriorityClass != target)
            {
                self.PriorityClass = target;
                Log.Information("Optima process priority set to {Priority}", target);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not change the Optima process priority");
        }
    }

    private void ToggleConsole()
    {
        if (_console is { IsVisible: true })
        {
            _console.Hide();
            return;
        }
        // Ownerless on purpose: the console must be able to sit on top of the game, not the app.
        _console ??= new ConsoleWindow { DataContext = _host!.Services.GetRequiredService<LogsViewModel>() };
        _console.Show();
    }

    private void KillGameFromHotkey()
    {
        var play = _host!.Services.GetRequiredService<PlayViewModel>();
        _ = play.KillGameCommand.ExecuteAsync(null);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        // Never leave temporary system changes behind, even on a UI crash (§18).
        TryEmergencyRestore();
        MessageBox.Show(
            "Optima hit an unexpected error and will close.\n\n" +
            "Any temporary system changes have been rolled back. Details were written to the log folder.",
            "Optima", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        if (_shutdown is not null)
        {
            _shutdown.ShutdownNow(1);
        }
        else
        {
            Shutdown(1);
        }
    }

    private void TryEmergencyRestore()
    {
        try
        {
            var recovery = _host?.Services.GetService<IRecoveryService>();
            if (recovery is null)
            {
                return;
            }
            // Off the dispatcher: this often runs because the UI thread is already unhappy, and
            // the restore's continuations must never need it. Bounded so a wedged device call
            // cannot hang exit.
            var restore = Task.Run(async () =>
            {
                var pending = await recovery.GetPendingAsync().ConfigureAwait(false);
                if (pending is not null)
                {
                    await recovery.RestoreAsync(pending).ConfigureAwait(false);
                }
            });
            if (!restore.Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Emergency restore timed out; the recovery prompt will appear on next start");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Emergency restore failed; the recovery prompt will appear on next start");
        }
    }

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(SingleInstanceActivateEventName);
            existing.Set();
        }
        catch
        {
            // The owner may be exiting right now; the user can just start Optima again.
        }
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceActivateEventName);
        var dispatcher = Dispatcher;
        Task.Run(async () =>
        {
            while (_activateEvent is { } evt)
            {
                try
                {
                    await Task.Run(evt.WaitOne).ConfigureAwait(true);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                _ = dispatcher.BeginInvoke(() =>
                {
                    var window = MainWindow;
                    if (window is null)
                    {
                        return;
                    }
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            }
        });
    }

    private void ReleaseSingleInstance()
    {
        _activateEvent?.Dispose();
        _activateEvent = null;
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may be held by a different thread; abandoning it is fine on process exit.
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        _shutdown?.Dispose();
        _theme?.Dispose();
        var hostStopped = false;
        try
        {
            // Bounded: a hung background service must never keep a dead instance alive,
            // which is how users ended up launching a second Optima over the first.
            hostStopped = _host?.StopAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(5)) == true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host shutdown reported an error");
        }
        if (hostStopped)
        {
            try
            {
                _host?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host dispose reported an error");
            }
        }
        else
        {
            Log.Error("Host shutdown stalled past 5 seconds; forcing exit");
        }
        ReleaseSingleInstance();
        Log.Information("Optima exited");
        Log.CloseAndFlush();
        if (!hostStopped)
        {
            Environment.Exit(e.ApplicationExitCode);
        }
        base.OnExit(e);
    }
}
