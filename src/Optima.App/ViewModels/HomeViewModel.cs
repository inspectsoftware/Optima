using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>HOME dashboard (§3): status grid, system facts, live performance tiles, PLAY button.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ISystemInfoService _systemInfo;
    private readonly IPerformanceMonitor _monitor;
    private readonly IPerformanceMetricsProvider _metrics;

    public HomeViewModel(
        StatusViewModel status,
        PlayViewModel play,
        ISystemInfoService systemInfo,
        IPerformanceMonitor monitor,
        IPerformanceMetricsProvider metrics)
    {
        Status = status;
        Play = play;
        _systemInfo = systemInfo;
        _monitor = monitor;
        _metrics = metrics;
        _monitor.MetricsUpdated += OnMetrics;
        _metrics.SampleArrived += OnFpsSample;
    }

    public StatusViewModel Status { get; }
    public PlayViewModel Play { get; }

    [ObservableProperty] private string _gpuText = "---";
    [ObservableProperty] private string _cpuText = "---";
    [ObservableProperty] private string _ramText = "---";
    [ObservableProperty] private string _windowsText = "---";

    [ObservableProperty] private string _cpuUsage = "---";
    [ObservableProperty] private string _gpuUsage = "---";
    [ObservableProperty] private string _ramUsage = "---";
    [ObservableProperty] private string _fpsText = "---";
    [ObservableProperty] private string _frametimeText = "---";
    [ObservableProperty] private string _gpuTempText = string.Empty;

    // Numeric counterparts driving the ASCII meters; the strings above are what gets read.
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _ramPercent;

    private DateTimeOffset _lastFpsSample = DateTimeOffset.MinValue;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
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

            // FPS goes stale when the game stops presenting.
            if (DateTimeOffset.UtcNow - _lastFpsSample > TimeSpan.FromSeconds(5))
            {
                FpsText = "---";
                FrametimeText = "---";
            }
        });
    }

    private void OnFpsSample(object? sender, (double Fps, double FrametimeMs) sample)
    {
        _lastFpsSample = DateTimeOffset.UtcNow;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            FpsText = $"{sample.Fps:F0}";
            FrametimeText = $"{sample.FrametimeMs:F1} ms";
        });
    }
}
