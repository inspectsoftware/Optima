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

    public static LoggingLevelSwitch LogLevelSwitch { get; } = new(LogEventLevel.Information);
    public static InAppLogSink LogSink { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();

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
        // Theme must be on the wall before the first window paints.
        var initialSettings = settingsService.GetSettingsAsync().GetAwaiter().GetResult();
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
            var pending = recovery.GetPendingAsync().GetAwaiter().GetResult();
            if (pending is not null)
            {
                recovery.RestoreAsync(pending).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Emergency restore failed; the recovery prompt will appear on next start");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        _shutdown?.Dispose();
        _theme?.Dispose();
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host shutdown reported an error");
        }
        Log.Information("Optima exited");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
