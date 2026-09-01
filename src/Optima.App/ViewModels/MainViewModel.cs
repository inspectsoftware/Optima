using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One sidebar row. <see cref="Key"/> doubles as the navigation command parameter.</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string index, string key, string? sectionHeader = null, string? iconKey = null)
    {
        Index = index;
        Key = key;
        SectionHeader = sectionHeader ?? "";
        IconKey = iconKey ?? (key.Length > 1 ? key[0] + key[1..].ToLowerInvariant() : key);
        Label = key.Length > 1 ? key[0] + key[1..].ToLowerInvariant() : key;
    }

    /// <summary>Row number; also the Alt+N shortcut it answers to.</summary>
    public string Index { get; }

    public string Key { get; }

    /// <summary>Display name of the row.</summary>
    public string Label { get; }

    /// <summary>Icon resource suffix (Themes/Icons.xaml "Icon.&lt;key&gt;").</summary>
    public string IconKey { get; }

    /// <summary>Group label rendered above this row when it opens a new sidebar section.</summary>
    public string SectionHeader { get; }

    [ObservableProperty]
    private bool _isActive;
}

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
    private readonly Core.Monitoring.GamePresenceService _presence;
    private readonly GameWatchService _gameWatch;
    private readonly Services.FirstRunFixService _firstRunFix;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        HomeViewModel home,
        PlayViewModel play,
        PerformanceViewModel performance,
        SessionsViewModel sessions,
        DisplayViewModel display,
        SystemViewModel system,
        CompViewModel comp,
        DiagnosticsViewModel diagnostics,
        LogsViewModel logs,
        SettingsViewModel settingsPage,
        DeveloperViewModel developer,
        NewsViewModel news,
        UpdateLogViewModel updateLog,
        LegalViewModel legal,
        StatusViewModel status,
        IRecoveryService recovery,
        SettingsService settings,
        IPerformanceMonitor monitor,
        ISessionStore sessionStore,
        IProcessMonitor processMonitor,
        Core.Monitoring.GamePresenceService presence,
        GameWatchService gameWatch,
        Services.FirstRunFixService firstRunFix,
        ILogger<MainViewModel> logger)
    {
        Home = home;
        Play = play;
        Performance = performance;
        Sessions = sessions;
        Display = display;
        System = system;
        Comp = comp;
        Legal = legal;
        Diagnostics = diagnostics;
        Logs = logs;
        SettingsPage = settingsPage;
        Developer = developer;
        News = news;
        UpdateLog = updateLog;
        Status = status;
        _recovery = recovery;
        _settings = settings;
        _monitor = monitor;
        _sessionStore = sessionStore;
        _processMonitor = processMonitor;
        _presence = presence;
        _gameWatch = gameWatch;
        _firstRunFix = firstRunFix;
        _logger = logger;
        _currentPage = home;
        _settings.SettingsChanged += (_, s) => DeveloperModeVisible = s.DeveloperMode;
    }

    public HomeViewModel Home { get; }
    public PlayViewModel Play { get; }
    public PerformanceViewModel Performance { get; }
    public SessionsViewModel Sessions { get; }
    public DisplayViewModel Display { get; }
    public SystemViewModel System { get; }
    public CompViewModel Comp { get; }
    public LegalViewModel Legal { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }
    public DeveloperViewModel Developer { get; }
    public NewsViewModel News { get; }
    public UpdateLogViewModel UpdateLog { get; }
    public StatusViewModel Status { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private bool _developerModeVisible;

    /// <summary>Path-style label for the current page, shown in the title bar.</summary>
    [ObservableProperty]
    private string _breadcrumb = "HOME";

    /// <summary>
    /// Sidebar rows. Data-driven rather than hand-written elements so the active marker
    /// follows every navigation route: mouse, Alt+N shortcut, or a programmatic jump.
    /// </summary>
    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new("01", "HOME", "PLAY") { IsActive = true },
        new("02", "PLAY"),
        new("03", "PERFORMANCE"),
        new("04", "SESSIONS"),
        new("05", "SYSTEM", "TUNE"),
        new("06", "COMP"),
        new("07", "DISPLAY"),
        new("08", "SETTINGS", "SUPPORT"),
        new("09", "DIAGNOSTICS"),
        new("10", "LOGS"),
        new("11", "NEWS"),
        new("12", "UPDATES"),
        new("13", "LEGAL"),
        new("14", "DEVELOPER"),
    ];

    /// <summary>The rail shows icons only; persisted in settings.</summary>
    [ObservableProperty]
    private bool _railCollapsed;

    [RelayCommand]
    private async Task ToggleRailAsync()
    {
        RailCollapsed = !RailCollapsed;
        try
        {
            var collapsed = RailCollapsed;
            await _settings.UpdateSettingsAsync(s => s with { RailCollapsed = collapsed });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the rail state");
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(string page)
    {
        Breadcrumb = page.ToUpperInvariant();
        foreach (var item in NavItems)
        {
            item.IsActive = string.Equals(item.Key, page, StringComparison.OrdinalIgnoreCase);
        }

        CurrentPage = page switch
        {
            "HOME" => Home,
            "PLAY" => Play,
            "PERFORMANCE" => Performance,
            "SESSIONS" => Sessions,
            "DISPLAY" => Display,
            "SYSTEM" => System,
            "COMP" => Comp,
            "LEGAL" => Legal,
            "DIAGNOSTICS" => Diagnostics,
            "LOGS" => Logs,
            "SETTINGS" => SettingsPage,
            "DEVELOPER" => Developer,
            "NEWS" => News,
            "UPDATES" => UpdateLog,
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
                case SessionsViewModel sess:
                    await sess.InitializeAsync();
                    break;
                case DisplayViewModel d:
                    await d.InitializeAsync();
                    break;
                case SystemViewModel s:
                    await s.InitializeAsync();
                    break;
                case CompViewModel c:
                    await c.InitializeAsync();
                    break;
                case LegalViewModel l:
                    await l.InitializeAsync();
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
                case NewsViewModel n:
                    await n.InitializeAsync();
                    break;
                case UpdateLogViewModel log:
                    await log.InitializeAsync();
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
                var restore = GlassDialog.Confirm(
                    Application.Current.MainWindow,
                    "Restore previous system settings?",
                    "Optima did not shut down cleanly last time and some system settings " +
                    "may still be modified (display, power plan, process tuning). " +
                    "Restoring puts the previous values back now.",
                    "Keep as is", "Restore");
                if (restore)
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
            RailCollapsed = settings.RailCollapsed;
            App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(settings.MinimumLogLevel);

            // 3. First-run wizard (§23).
            if (!settings.FirstRunCompleted)
            {
                var wizard = new SetupWizardWindow { Owner = Application.Current.MainWindow };
                var wizardViewModel = new SetupWizardViewModel(Status, Diagnostics, _settings, _firstRunFix);
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
            await _presence.StartAsync();
            await _gameWatch.StartAsync();

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
