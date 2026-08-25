using System.Windows;
using System.Windows.Threading;
using Optima.App.Logging;
using Optima.App.ViewModels;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Optima.App;

public partial class App : Application
{
    private IHost? _host;

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
        window.Show();

        _ = mainViewModel.InitializeAsync();
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
