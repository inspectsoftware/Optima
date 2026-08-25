using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using COPSBootstrapper.App.Views;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.App.ViewModels;

/// <summary>
/// Application shell: sidebar navigation, startup sequence (crash recovery prompt → first-run
/// wizard → detection + monitors), and page lifetimes.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IRecoveryService _recovery;
    private readonly SettingsService _settings;
    private readonly IPerformanceMonitor _monitor;
    private readonly ISessionStore _sessionStore;
    private readonly IProcessMonitor _processMonitor;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        HomeViewModel home,
        PlayViewModel play,
        PerformanceViewModel performance,
        DisplayViewModel display,
        SystemViewModel system,
        DiagnosticsViewModel diagnostics,
        LogsViewModel logs,
        SettingsViewModel settingsPage,
        DeveloperViewModel developer,
        StatusViewModel status,
        IRecoveryService recovery,
        SettingsService settings,
        IPerformanceMonitor monitor,
        ISessionStore sessionStore,
        IProcessMonitor processMonitor,
        ILogger<MainViewModel> logger)
    {
        Home = home;
        Play = play;
        Performance = performance;
        Display = display;
        System = system;
        Diagnostics = diagnostics;
        Logs = logs;
        SettingsPage = settingsPage;
        Developer = developer;
        Status = status;
        _recovery = recovery;
        _settings = settings;
        _monitor = monitor;
        _sessionStore = sessionStore;
        _processMonitor = processMonitor;
        _logger = logger;
        _currentPage = home;
        _settings.SettingsChanged += (_, s) => DeveloperModeVisible = s.DeveloperMode;
    }

    public HomeViewModel Home { get; }
    public PlayViewModel Play { get; }
    public PerformanceViewModel Performance { get; }
    public DisplayViewModel Display { get; }
    public SystemViewModel System { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }
    public DeveloperViewModel Developer { get; }
    public StatusViewModel Status { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private bool _developerModeVisible;

    [RelayCommand]
    private async Task NavigateAsync(string page)
    {
        CurrentPage = page switch
        {
            "HOME" => Home,
            "PLAY" => Play,
            "PERFORMANCE" => Performance,
            "DISPLAY" => Display,
            "SYSTEM" => System,
            "DIAGNOSTICS" => Diagnostics,
            "LOGS" => Logs,
            "SETTINGS" => SettingsPage,
            "DEVELOPER" => Developer,
            _ => Home,
        };

        // Page-specific refresh on entry, kept quick and cancel-safe.
        try
        {
            switch (CurrentPage)
            {
                case PerformanceViewModel p:
                    await p.InitializeAsync();
                    break;
                case DisplayViewModel d:
                    await d.InitializeAsync();
                    break;
                case SystemViewModel s:
                    await s.InitializeAsync();
                    break;
                case DiagnosticsViewModel diag:
                    await diag.InitializeAsync();
                    break;
                case SettingsViewModel st:
                    await st.InitializeAsync();
                    break;
                case DeveloperViewModel dev:
                    await dev.RefreshAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Page initialization failed for {Page}", page);
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            // 1. Crash recovery (§18): offer to restore before anything else touches the system.
            var pending = await _recovery.GetPendingAsync();
            if (pending is not null)
            {
                var restore = MessageBox.Show(
                    "COPS Bootstrapper did not shut down cleanly last time and some system settings " +
                    "may still be modified (display, power plan, process tuning).\n\n" +
                    "Restore the previous system settings now?",
                    "Restore previous system settings",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (restore == MessageBoxResult.Yes)
                {
                    await _recovery.RestoreAsync(pending);
                }
                else
                {
                    await _recovery.ClearPendingAsync();
                }
            }

            // 2. Settings-driven state.
            var settings = await _settings.GetSettingsAsync();
            DeveloperModeVisible = settings.DeveloperMode;
            App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(settings.MinimumLogLevel);

            // 3. First-run wizard (§23).
            if (!settings.FirstRunCompleted)
            {
                var wizard = new SetupWizardWindow { Owner = Application.Current.MainWindow };
                var wizardViewModel = new SetupWizardViewModel(Status, Diagnostics, _settings);
                wizard.DataContext = wizardViewModel;
                _ = wizardViewModel.RunDetectionAsync();
                wizard.ShowDialog();
            }

            // 4. Background services.
            await _sessionStore.InitializeAsync();
            await Status.RefreshAsync();
            await Home.InitializeAsync();
            await Play.InitializeAsync();
            await _monitor.StartAsync();

            // 5. Keep the "running" badge and game process ids current.
            _ = Task.Run(BackgroundStatusLoopAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialization failed");
        }
    }

    private async Task BackgroundStatusLoopAsync()
    {
        while (Application.Current is not null)
        {
            try
            {
                var tracked = await _processMonitor.GetTrackedProcessesAsync();
                _monitor.SetGameProcessIds(tracked
                    .Where(p => p.Kind is TrackedProcessKind.Emulator or TrackedProcessKind.GameWindow)
                    .Select(p => p.ProcessId)
                    .ToList());

                await Application.Current.Dispatcher.InvokeAsync(async () => await Status.RefreshAsync());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background status tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
}
