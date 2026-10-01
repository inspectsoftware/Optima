using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.News;
using Optima.Core.Stats;

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
        ISystemInfoService systemInfo,
        IPerformanceMonitor monitor,
        CopsNewsService news,
        SettingsService settings,
        Services.PlayerSwitcherService players)
    {
        Status = status;
        Play = play;
        Player = playerStats;
        _systemInfo = systemInfo;
        _monitor = monitor;
        _news = news;
        _settings = settings;
        _players = players;
        _monitor.MetricsUpdated += OnMetrics;
        // The friends strip must follow Settings edits immediately, not only on next visit.
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshFriendsCommand.ExecuteAsync(null));
    }

    public StatusViewModel Status { get; }
    public PlayViewModel Play { get; }

    /// <summary>The player panel shown directly below LAUNCH.</summary>
    public PlayerStatsViewModel Player { get; }

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
            HasFriends = Friends.Count > 0;
            FriendsStatus = Friends.Count == 0 ? "Add friends or clanmates from the PLAYER panel in Settings." : string.Empty;
        }
        catch (Exception)
        {
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
