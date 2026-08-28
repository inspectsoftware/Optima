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
    private ConsoleWindow? _console;
    private OverlayController? _overlay;

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

        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = mainViewModel };
        MainWindow = window;
        // The hidden console window must not keep the app alive after the main window closes.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

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

        _tray = new TrayService(window, _host.Services.GetRequiredService<SettingsService>());
        _tray.AttachOrchestrator(_host.Services.GetRequiredService<LaunchOrchestrator>());
        // Same route as Ctrl+Alt+K, so the result text lands in the UI either way.
        _tray.TerminateGameRequested += KillGameFromHotkey;
        _tray.NavigateRequested += page =>
        {
            _tray!.ShowMainWindow();
            _ = mainViewModel.NavigateCommand.ExecuteAsync(page);
        };

        _ = mainViewModel.InitializeAsync();
    }

    /// <summary>Global Alt+F9: floating log console over whatever is on screen, without stealing focus.</summary>
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

    /// <summary>Global Ctrl+Alt+K routes through the same command as the kill buttons, so the
    /// result text lands in the UI either way.</summary>
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
        Shutdown(1);
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
