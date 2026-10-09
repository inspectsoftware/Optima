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

    /// <summary>How many things on the page want looking at; shown as a count on the row when above zero.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int _badgeCount;

    public bool HasBadge => BadgeCount > 0;
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
    private readonly ProfileService _profiles;
    private readonly IDriverInstaller _driverInstaller;

    public MainViewModel(
        HomeViewModel home,
        PlayViewModel play,
        PerformanceViewModel performance,
        SessionsViewModel sessions,
        DisplayViewModel display,
        CompViewModel comp,
        BoostViewModel boost,
        DebugViewModel debug,
        SettingsViewModel settingsPage,
        DeveloperViewModel developer,
        NewsViewModel news,
        UpdateLogViewModel updateLog,
        LegalViewModel legal,
        StatusViewModel status,
        Services.ToastService toasts,
        IRecoveryService recovery,
        SettingsService settings,
        Services.PlayerSwitcherService players,
        IPerformanceMonitor monitor,
        ISessionStore sessionStore,
        IProcessMonitor processMonitor,
        Core.Monitoring.GamePresenceService presence,
        GameWatchService gameWatch,
        Services.FirstRunFixService firstRunFix,
        ProfileService profiles,
        IDriverInstaller driverInstaller,
        ILogger<MainViewModel> logger)
    {
        _profiles = profiles;
        _driverInstaller = driverInstaller;
        Home = home;
        Play = play;
        Performance = performance;
        Sessions = sessions;
        Display = display;
        Comp = comp;
        Boost = boost;
        Legal = legal;
        Debug = debug;
        SettingsPage = settingsPage;
        Developer = developer;
        News = news;
        UpdateLog = updateLog;
        Status = status;
        Toasts = toasts;
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

        // The issue count rides on the rail, so a problem is visible from whatever page is open.
        var debugItem = NavItems.First(item => item.Key == "DEBUG");
        debugItem.BadgeCount = Debug.Issues.AttentionCount;
        Debug.Issues.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IssuesViewModel.AttentionCount))
            {
                debugItem.BadgeCount = Debug.Issues.AttentionCount;
            }
        };
    }

    public HomeViewModel Home { get; }
    public PlayViewModel Play { get; }
    public PerformanceViewModel Performance { get; }
    public SessionsViewModel Sessions { get; }
    public DisplayViewModel Display { get; }
    public CompViewModel Comp { get; }
    public BoostViewModel Boost { get; }
    public LegalViewModel Legal { get; }
    public DebugViewModel Debug { get; }
    public SettingsViewModel SettingsPage { get; }
    public DeveloperViewModel Developer { get; }
    public NewsViewModel News { get; }
    public UpdateLogViewModel UpdateLog { get; }
    public StatusViewModel Status { get; }

    /// <summary>The notices shown over the page area, whatever page is open.</summary>
    public Services.ToastService Toasts { get; }

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

    /// <summary>
    /// The X beside an account in the switcher: takes it off the list. The identity that is active
    /// stays active; only its saved entry goes, and Settings can save it again. The list rebuilds
    /// itself from the settings change.
    /// </summary>
    // Takes anything, on purpose. The button sits in the switcher's item template, and that
    // template is also what the closed box shows: while the list is being rebuilt the box holds an
    // empty text and no account. A command typed to PlayerAccount throws on that the moment WPF
    // asks whether it can run, in the middle of the rebuild, and the list was left empty.
    [RelayCommand]
    private async Task RemoveAccountAsync(object? row)
    {
        if (row is not PlayerAccount { Key.Length: > 0 } account)
        {
            return;
        }
        try
        {
            await _players.RemoveAccountAsync(account.Key);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Removing a saved account failed");
            Toasts.Show("Account not removed", "The settings could not be saved: " + ex.Message, Services.ToastKind.Warn);
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
        new("09", "DEBUG"),
        new("10", "NEWS"),
        new("11", "UPDATES"),
        new("12", "LEGAL"),
        new("13", "DEVELOPER"),
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
        // DEBUG took over from two pages that are still asked for by name (a HOME widget, an old
        // shortcut, a link in an error): each of them now means one of its tabs.
        if (DebugTabFor(page) is { } tab)
        {
            Debug.SelectedTab = tab;
            page = "DEBUG";
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
            "DEBUG" => Debug,
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
                case DebugViewModel debug:
                    await debug.InitializeAsync();
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

    private static DebugTab? DebugTabFor(string page) => page.ToUpperInvariant() switch
    {
        "DIAGNOSTICS" => DebugTab.Checks,
        "LOGS" => DebugTab.Log,
        _ => null,
    };

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
            App.LogLevelSwitch.MinimumLevel = LogStreamViewModel.ToSerilogLevel(settings.MinimumLogLevel);
            await ReloadSavedAccountsAsync();
            // SettingsChanged can fire from any thread; the collections must be touched on the UI one.
            // Posted, never awaited: a save that lands while the app closes finds no application or
            // a dispatcher that has stopped, and an await on either threw on a pool thread.
            _settings.SettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _ = ReloadSavedAccountsAsync());

            if (!settings.FirstRunCompleted)
            {
                var wizard = new SetupWizardWindow { Owner = Application.Current.MainWindow };
                var wizardViewModel = new SetupWizardViewModel(Status, Debug.Checks, _settings, _firstRunFix);
                wizard.DataContext = wizardViewModel;
                _ = wizardViewModel.RunDetectionAsync();
                wizard.ShowDialog();
            }

            // Before the PLAY page loads its profiles: it selects by name, and a selection that
            // names one of the retired built-ins has to be moved over first, not fall back to Default.
            await MoveDisplayChoiceAsync(freshInstall: !settings.FirstRunCompleted);

            try
            {
                await _sessionStore.InitializeAsync();
            }
            catch (Exception ex)
            {
                // A session history that cannot be opened must not take the status rows, the
                // profiles and the watchdog down with it; the Sessions page reports it by itself.
                _logger.LogError(ex, "The session store could not be opened");
            }
            // Side by side: none of these reads what another one loads, and the status probes ask
            // WMI, so everything queued behind them used to wait half a second for nothing.
            var status = Status.RefreshAsync();
            var home = Home.InitializeAsync();
            var play = Play.InitializeAsync();
            _ = Play.InitializeCrashBannerAsync();
            await _presence.StartAsync();
            await _gameWatch.StartAsync();
            await Task.WhenAll(status, home, play);

            _ = Task.Run(BackgroundStatusLoopAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialization failed");
        }
    }

    /// <summary>
    /// Once, on the first start of a build that has the display as one choice: fills that choice
    /// in from the profile that was selected (see <see cref="DisplayChoice"/>). A failure leaves
    /// it for the next start, and until then a launch follows the profile as it always did.
    /// </summary>
    private async Task MoveDisplayChoiceAsync(bool freshInstall)
    {
        try
        {
            if ((await _settings.GetSettingsAsync()).VirtualDisplayEnabled is not null)
            {
                return;
            }
            var profiles = await _profiles.GetProfilesAsync();
            var driverInstalled = await _driverInstaller.GetStateAsync().WaitAsync(TimeSpan.FromSeconds(5)) == DriverState.Installed;
            await _settings.UpdateSettingsAsync(s => DisplayChoice.Migrate(s, profiles, freshInstall, driverInstalled));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The display choice could not be moved out of the profiles yet");
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

                // The rows this refreshes are only drawn in the window. While it is in the tray or
                // minimized, which is where it sits through a game, the display sweeps and the hops
                // to the UI thread are for nobody; the app refreshes once as the window returns.
                if (Services.RepairMoment.WindowVisible)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => Status.RefreshLiveAsync()).Task.Unwrap();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background status tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
}
