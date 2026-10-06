using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One sidebar row.</summary>
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

    public string Index { get; }

    public string Key { get; }

    public string Label { get; }

    public string IconKey { get; }

    public string SectionHeader { get; }

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// Application shell: sidebar navigation, startup sequence (crash recovery prompt → first-run wizard → detection +
/// monitors), and page lifetimes.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IRecoveryService _recovery;
    private readonly SettingsService _settings;
    private readonly Services.PlayerSwitcherService _players;
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
        CompViewModel comp,
        BoostViewModel boost,
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
        Services.PlayerSwitcherService players,
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
        Comp = comp;
        Boost = boost;
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
        _players = players;
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
    public CompViewModel Comp { get; }
    public BoostViewModel Boost { get; }
    public LegalViewModel Legal { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }
    public DeveloperViewModel Developer { get; }
    public NewsViewModel News { get; }
    public UpdateLogViewModel UpdateLog { get; }
    public StatusViewModel Status { get; }

    /// <summary>Saved identities for the title-bar account switcher.</summary>
    public ObservableCollection<PlayerAccount> SavedAccounts { get; } = [];

    [ObservableProperty]
    private PlayerAccount? _activeAccount;

    [RelayCommand]
    private async Task SwitchAccountAsync(PlayerAccount? account)
    {
        // The placeholder (empty key) and the already-active identity never switch; snap
        // the selection back so the closed combobox keeps showing the active account.
        if (account is null || account.Key.Length == 0)
        {
            OnPropertyChanged(nameof(ActiveAccount));
            return;
        }
        var settings = await _settings.GetSettingsAsync();
        if (account.Matches(settings.PlayerIgn, settings.PlayerAccountId))
        {
            OnPropertyChanged(nameof(ActiveAccount));
            return;
        }
        try
        {
            await _players.SwitchToAsync(account);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching the active account failed");
        }
    }

    /// <summary>Placeholder shown in the switcher when there is nothing else to switch to.</summary>
    public static readonly PlayerAccount NoOtherAccountPlaceholder =
        new() { Key = string.Empty, Ign = "No other accounts added" };

    private async Task ReloadSavedAccountsAsync()
    {
        var settings = await _settings.GetSettingsAsync();
        SavedAccounts.Clear();
        foreach (var account in settings.SavedAccounts)
        {
            SavedAccounts.Add(account with
            {
                IsActive = string.Equals(account.Ign.Trim(), settings.PlayerIgn.Trim(), StringComparison.OrdinalIgnoreCase),
            });
        }
        // The dropdown must always open with something meaningful, even for a single account.
        if (SavedAccounts.Count <= 1)
        {
            SavedAccounts.Add(NoOtherAccountPlaceholder);
        }
        ActiveAccount = SavedAccounts.FirstOrDefault(a => a.IsActive)
            ?? SavedAccounts.FirstOrDefault(a => a.Matches(settings.PlayerIgn, settings.PlayerAccountId));
    }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private bool _developerModeVisible;

    [ObservableProperty]
    private string _breadcrumb = "HOME";

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new("01", "HOME", "PLAY") { IsActive = true },
        new("02", "PLAY"),
        new("03", "PERFORMANCE"),
        new("04", "SESSIONS"),
        new("05", "COMP", "TUNE"),
        new("06", "BOOST"),
        new("07", "DISPLAY"),
        new("08", "SETTINGS", "SUPPORT"),
        new("09", "DIAGNOSTICS"),
        new("10", "LOGS"),
        new("11", "NEWS"),
        new("12", "UPDATES"),
        new("13", "LEGAL"),
        new("14", "DEVELOPER"),
    ];

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
        // The rail hides this page with developer mode off; its shortcut has to respect that too.
        if (!DeveloperModeVisible && string.Equals(page, "DEVELOPER", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
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
            "COMP" => Comp,
            "BOOST" => Boost,
            "LEGAL" => Legal,
            "DIAGNOSTICS" => Diagnostics,
            "LOGS" => Logs,
            "SETTINGS" => SettingsPage,
            "DEVELOPER" => Developer,
            "NEWS" => News,
            "UPDATES" => UpdateLog,
            _ => Home,
        };

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
                case CompViewModel c:
                    await c.InitializeAsync();
                    break;
                case BoostViewModel b:
                    await b.InitializeAsync();
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

            var settings = await _settings.GetSettingsAsync();
            DeveloperModeVisible = settings.DeveloperMode;
            RailCollapsed = settings.RailCollapsed;
            App.LogLevelSwitch.MinimumLevel = LogsViewModel.ToSerilogLevel(settings.MinimumLogLevel);
            await ReloadSavedAccountsAsync();
            // SettingsChanged can fire from any thread; the collections must be touched on the UI one.
            _settings.SettingsChanged += async (_, _) => await Application.Current.Dispatcher.InvokeAsync(ReloadSavedAccountsAsync);

            if (!settings.FirstRunCompleted)
            {
                var wizard = new SetupWizardWindow { Owner = Application.Current.MainWindow };
                var wizardViewModel = new SetupWizardViewModel(Status, Diagnostics, _settings, _firstRunFix);
                wizard.DataContext = wizardViewModel;
                _ = wizardViewModel.RunDetectionAsync();
                wizard.ShowDialog();
            }

            await _sessionStore.InitializeAsync();
            await Status.RefreshAsync();
            await Home.InitializeAsync();
            await Play.InitializeAsync();
            _ = Play.InitializeCrashBannerAsync();
            await _presence.StartAsync();
            await _gameWatch.StartAsync();

            _ = Task.Run(BackgroundStatusLoopAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialization failed");
        }
    }

    private async Task BackgroundStatusLoopAsync()
    {
        var hadGamePids = false;
        while (Application.Current is not null)
        {
            try
            {
                if (_presence.Current != Core.Monitoring.GamePresence.NotRunning)
                {
                    var tracked = await _processMonitor.GetTrackedProcessesAsync();
                    _monitor.SetGameProcessIds(tracked
                        .Where(p => p.Kind is TrackedProcessKind.Emulator or TrackedProcessKind.GameWindow)
                        .Select(p => p.ProcessId)
                        .ToList());
                    hadGamePids = true;
                }
                else if (hadGamePids)
                {
                    _monitor.SetGameProcessIds([]);
                    hadGamePids = false;
                }

                await Application.Current.Dispatcher.InvokeAsync(() => Status.RefreshLiveAsync()).Task.Unwrap();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background status tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
}
