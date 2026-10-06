using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.News;

namespace Optima.App.ViewModels;

/// <summary>HOME dashboard (§3): status grid, system facts, live performance tiles, PLAY button.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ISystemInfoService _systemInfo;
    private readonly IPerformanceMonitor _monitor;
    private readonly CopsNewsService _news;
    private readonly SettingsService _settings;
    private readonly Services.PlayerSwitcherService _players;

    public HomeViewModel(
        StatusViewModel status,
        PlayViewModel play,
        PlayerStatsViewModel playerStats,
        SessionsViewModel sessions,
        DisplayViewModel display,
        CompViewModel comp,
        NewsViewModel newsPage,
        DiagnosticsViewModel diagnostics,
        ISystemInfoService systemInfo,
        IPerformanceMonitor monitor,
        CopsNewsService news,
        SettingsService settings,
        Services.PlayerSwitcherService players)
    {
        Status = status;
        Play = play;
        Player = playerStats;
        Sessions = sessions;
        Display = display;
        Comp = comp;
        NewsPage = newsPage;
        Diagnostics = diagnostics;
        _systemInfo = systemInfo;
        _monitor = monitor;
        _news = news;
        _settings = settings;
        _players = players;
        _monitor.MetricsUpdated += OnMetrics;
        // The friends strip must follow Settings edits immediately, not only on next visit.
        _settings.SettingsChanged += OnSettingsChanged;
        BuildWidgetCatalog();
    }

    /// <summary>Signature of the tracked list the strip is built from; a save that does not touch it changes nothing.</summary>
    private string _friendsSignature = string.Empty;
    private bool _friendsRefreshFailed;

    // ---------------------------------------------------------------- HOME widget board

    /// <summary>The widgets on HOME, in order. Every template binds through the item's Host.</summary>
    public ObservableCollection<HomeWidgetItem> Widgets { get; } = [];

    /// <summary>Every widget the app offers, grouped by tab, for EDIT WIDGETS mode.</summary>
    public ObservableCollection<HomeWidgetGroup> WidgetCatalog { get; } = [];

    /// <summary>True while the board is being rearranged: chrome appears and drag moves widgets.</summary>
    [ObservableProperty] private bool _isEditingWidgets;

    /// <summary>Why an edit was refused (the ten-widget limit), shown next to the panel.</summary>
    [ObservableProperty] private string _widgetMessage = string.Empty;

    private readonly Dictionary<string, HomeWidgetItem> _widgetIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _widgetSourcesStarted = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void ToggleEditWidgets()
    {
        IsEditingWidgets = !IsEditingWidgets;
        WidgetMessage = string.Empty;
    }

    [RelayCommand]
    private void AddWidget(HomeWidgetItem? item) => InsertWidget(item, Widgets.Count);

    [RelayCommand]
    private void RemoveWidget(HomeWidgetItem? item) => RemoveFromHome(item?.Definition.Id);

    /// <summary>Puts a widget on HOME at a position, or moves it when it is already there.</summary>
    public bool InsertWidget(HomeWidgetItem? item, int index)
    {
        if (item is null)
        {
            return false;
        }

        if (item.IsOnHome)
        {
            MoveTo(item, index);
            return true;
        }

        if (Widgets.Count >= HomeWidgetCatalog.MaxOnHome)
        {
            WidgetMessage = $"HOME holds {HomeWidgetCatalog.MaxOnHome} widgets at most. " +
                "Take one off HOME first, or drop this onto the position it should replace.";
            return false;
        }

        Widgets.Insert(Math.Clamp(index, 0, Widgets.Count), item);
        item.IsOnHome = true;
        WidgetMessage = string.Empty;
        StartWidgetSources();
        PersistWidgetLayout();
        return true;
    }

    /// <summary>Moves an on-HOME widget to a new position (drag to reorder).</summary>
    public void MoveTo(HomeWidgetItem? item, int index)
    {
        if (item is null)
        {
            return;
        }

        var current = Widgets.IndexOf(item);
        if (current < 0)
        {
            return;
        }

        var target = Math.Clamp(index, 0, Widgets.Count - 1);
        if (target == current)
        {
            return;
        }

        Widgets.Move(current, target);
        WidgetMessage = string.Empty;
        PersistWidgetLayout();
    }

    /// <summary>Where a drop lands: adds the widget when it comes from the panel, moves it otherwise.</summary>
    public void MoveOrAdd(string id, int index)
    {
        if (_widgetIndex.TryGetValue(id, out var item))
        {
            InsertWidget(item, index);
        }
    }

    public void RemoveFromHome(string? id)
    {
        if (id is null || !_widgetIndex.TryGetValue(id, out var item) || !item.IsOnHome)
        {
            return;
        }

        Widgets.Remove(item);
        item.IsOnHome = false;
        WidgetMessage = string.Empty;
        PersistWidgetLayout();
    }

    private void BuildWidgetCatalog()
    {
        _widgetIndex.Clear();
        foreach (var definition in HomeWidgetCatalog.All)
        {
            _widgetIndex[definition.Id] = new HomeWidgetItem(definition, this);
        }

        WidgetCatalog.Clear();
        foreach (var group in HomeWidgetCatalog.All.GroupBy(definition => definition.Tab))
        {
            WidgetCatalog.Add(new HomeWidgetGroup(group.Key, group.Select(definition => _widgetIndex[definition.Id])));
        }
    }

    /// <summary>The saved layout, or the default one for a profile that has never edited it.</summary>
    private IReadOnlyList<string> SavedLayout(AppSettings settings)
        => settings.HomeWidgets is null
            ? HomeWidgetCatalog.Default
            : settings.HomeWidgets.Where(id => _widgetIndex.ContainsKey(id)).ToList();

    private bool LayoutMatches(IReadOnlyList<string> layout)
        => layout.Count == Widgets.Count
            && layout.SequenceEqual(Widgets.Select(widget => widget.Definition.Id), StringComparer.OrdinalIgnoreCase);

    private void ApplyWidgetLayout(IReadOnlyList<string> layout)
    {
        foreach (var item in _widgetIndex.Values)
        {
            item.IsOnHome = false;
        }

        Widgets.Clear();
        foreach (var id in layout
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(HomeWidgetCatalog.MaxOnHome))
        {
            if (_widgetIndex.TryGetValue(id, out var item) && !item.IsOnHome)
            {
                item.IsOnHome = true;
                Widgets.Add(item);
            }
        }

        StartWidgetSources();
    }

    private void PersistWidgetLayout()
        => _ = _settings.UpdateSettingsAsync(settings => settings with
        {
            HomeWidgets = Widgets.Select(widget => widget.Definition.Id).ToList(),
        });

    /// <summary>
    /// Wakes the page behind each widget that is actually on HOME, once per widget: a card that
    /// shows sessions, news or diagnostics has nothing to show until its page has loaded, and the
    /// pages otherwise only load when they are opened. Quietly, because a widget must never be able
    /// to take HOME down with it.
    /// </summary>
    private void StartWidgetSources()
    {
        foreach (var widget in Widgets)
        {
            if (!_widgetSourcesStarted.Add(widget.Definition.Id))
            {
                continue;
            }

            switch (widget.Definition.Id)
            {
                case "sessions":
                case "trends":
                    _ = QuietlyAsync(Sessions.InitializeAsync());
                    break;
                case "display":
                    _ = QuietlyAsync(Display.InitializeAsync());
                    break;
                case "comp":
                    _ = QuietlyAsync(Comp.InitializeAsync());
                    break;
                case "news":
                    _ = QuietlyAsync(NewsPage.InitializeAsync());
                    break;
                case "diagnostics":
                    _ = QuietlyAsync(Diagnostics.InitializeAsync());
                    break;
            }
        }
    }

    private static async Task QuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The card shows whatever the page managed to load; a failure is not HOME's problem.
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // SettingsChanged is raised on whatever thread finished the save; the board is bound to the UI.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSettingsChanged(sender, settings));
            return;
        }

        // The layout can also arrive from a save made elsewhere (a second window, a profile switch).
        var layout = SavedLayout(settings);
        if (!LayoutMatches(layout))
        {
            ApplyWidgetLayout(layout);
        }

        // Every toggle in the app saves settings, and this refresh costs a profile lookup per tracked
        // player, so it only runs for a change it can actually show — or to retry a failed attempt.
        var signature = string.Join(
            '\n',
            settings.TrackedPlayers.Select(t => t.Key + "|" + t.Ign + "|" + t.AccountId));
        if (!_friendsRefreshFailed && string.Equals(signature, _friendsSignature, StringComparison.Ordinal))
        {
            return;
        }
        _friendsSignature = signature;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshFriendsCommand.ExecuteAsync(null));
    }

    public StatusViewModel Status { get; }
    public PlayViewModel Play { get; }

    /// <summary>The player panel shown directly below LAUNCH.</summary>
    public PlayerStatsViewModel Player { get; }

    /// <summary>The pages whose content widgets can borrow; a widget binds through its item's Host.</summary>
    public SessionsViewModel Sessions { get; }
    public DisplayViewModel Display { get; }
    public CompViewModel Comp { get; }
    public NewsViewModel NewsPage { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Friends and clanmates tracked alongside the main account.</summary>
    public ObservableCollection<Optima.Core.Stats.TrackedPlayerRow> Friends { get; } = [];

    [ObservableProperty] private string _friendsStatus = string.Empty;
    [ObservableProperty] private bool _hasFriends;

    [RelayCommand]
    private async Task RefreshFriendsAsync()
    {
        try
        {
            var rows = await _players.GetTrackedRowsAsync();
            Friends.Clear();
            foreach (var row in rows)
            {
                Friends.Add(row);
            }
            _friendsRefreshFailed = false;
            HasFriends = Friends.Count > 0;
            FriendsStatus = Friends.Count == 0 ? "Add friends or clanmates from the PLAYER panel in Settings." : string.Empty;
        }
        catch (Exception)
        {
            // Remembered so the next settings save retries instead of leaving the strip empty.
            _friendsRefreshFailed = true;
            FriendsStatus = "Could not reach the stats API.";
        }
    }

    [ObservableProperty] private string _gpuText = "---";
    [ObservableProperty] private string _cpuText = "---";
    [ObservableProperty] private string _ramText = "---";
    [ObservableProperty] private string _windowsText = "---";

    [ObservableProperty] private string _cpuUsage = "---";
    [ObservableProperty] private string _gpuUsage = "---";
    [ObservableProperty] private string _ramUsage = "---";
    [ObservableProperty] private string _gpuTempText = string.Empty;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _ramPercent;

    [ObservableProperty] private string _gameUpdateBanner = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ApplyWidgetLayout(SavedLayout(await _settings.GetSettingsAsync(ct)));

        _ = Task.Run(() => CheckGameVersionAsync(ct), CancellationToken.None);
        _ = Player.InitializeAsync(ct);
        _ = RefreshFriendsCommand.ExecuteAsync(null);

        var inventory = await _systemInfo.GetInventoryAsync(ct);
        var gpu = inventory.Gpus
            .Where(g => !g.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Vendor == GpuVendor.Nvidia)
            .FirstOrDefault();
        GpuText = gpu?.Name ?? "Unknown";
        CpuText = inventory.CpuName;
        RamText = $"{inventory.TotalRamBytes / (1024.0 * 1024 * 1024):F0} GB";
        WindowsText = inventory.WindowsVersion;
    }

    private async Task CheckGameVersionAsync(CancellationToken ct)
    {
        try
        {
            var latest = CopsNewsParser.LatestLiveVersion(await _news.GetEntriesAsync(ct));
            if (latest is null)
            {
                return;
            }
            var stored = (await _settings.GetSettingsAsync(ct)).LastKnownGameVersion;
            if (stored.Length > 0 && !string.Equals(stored, latest, StringComparison.Ordinal))
            {
                var banner =
                    $"Critical Ops updated to {latest}. Optima has not been validated against this " +
                    "version yet; the overlay, tracking and saved profiles may need a re-check.";
                Application.Current?.Dispatcher.BeginInvoke(() => GameUpdateBanner = banner);
            }
            if (!string.Equals(stored, latest, StringComparison.Ordinal))
            {
                await _settings.UpdateSettingsAsync(s => s with { LastKnownGameVersion = latest }, ct);
            }
        }
        catch
        {
            // The banner is a bonus; a failed check must never disturb startup.
        }
    }

    private void OnMetrics(object? sender, HardwareMetrics metrics)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuUsage = $"{metrics.CpuUtilizationPercent:F0}%";
            GpuUsage = $"{metrics.GpuUtilizationPercent:F0}%";
            RamUsage = $"{metrics.RamUsedBytes / (1024.0 * 1024 * 1024):F1}G";
            GpuTempText = metrics.GpuTemperatureCelsius is { } temp ? $"{temp:F0}°C" : string.Empty;

            CpuPercent = metrics.CpuUtilizationPercent;
            GpuPercent = metrics.GpuUtilizationPercent;
            RamPercent = metrics.RamTotalBytes > 0
                ? 100.0 * metrics.RamUsedBytes / metrics.RamTotalBytes
                : 0;
        });
    }
}
