using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed record MonitorRow(string DeviceName, string Name, string Mode);

/// <summary>SYSTEM page (§4/§11/§16): hardware inventory, virtualization facts, live network quality.</summary>
public sealed partial class SystemViewModel : ObservableObject
{
    private static readonly TimeSpan NetworkStaleness = TimeSpan.FromSeconds(5);
    private const string NetworkIdleText = "not measuring · starts with a game session";

    private readonly ISystemInfoService _systemInfo;
    private readonly SettingsService _settings;
    private DateTimeOffset _lastNetworkSample = DateTimeOffset.MinValue;

    public SystemViewModel(ISystemInfoService systemInfo, SettingsService settings, INetworkQualityMonitor network)
    {
        _systemInfo = systemInfo;
        _settings = settings;
        network.SampleArrived += OnNetworkSample;

        var staleness = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        staleness.Tick += (_, _) =>
        {
            if (DateTimeOffset.Now - _lastNetworkSample > NetworkStaleness)
            {
                NetworkStatus = NetworkIdleText;
            }
        };
        staleness.Start();
    }

    [ObservableProperty] private string _networkStatus = NetworkIdleText;

    private void OnNetworkSample(object? sender, NetworkQualitySample sample)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _lastNetworkSample = DateTimeOffset.Now;
            var suffix = sample.IsReferenceHost ? " [ REF HOST ] link quality" : $" · {sample.Target}";
            NetworkStatus = $"{sample.PingMs:F0} ms · {sample.JitterMs:F1} ms jitter · {sample.PacketLossPct:F1}% loss{suffix}";
        });
    }

    public ObservableCollection<InfoRow> HardwareRows { get; } = [];
    public ObservableCollection<InfoRow> VirtualizationRows { get; } = [];
    public ObservableCollection<MonitorRow> Displays { get; } = [];

    [ObservableProperty] private bool _isLoading;

    public async Task InitializeAsync(CancellationToken ct = default) => await RefreshAsync(ct);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var inventory = await _systemInfo.GetInventoryAsync(ct);

            HardwareRows.Clear();
            HardwareRows.Add(new InfoRow("CPU", $"{inventory.CpuName} ({inventory.CpuCores}C/{inventory.CpuThreads}T)"));
            foreach (var gpu in inventory.Gpus)
            {
                var vram = gpu.VramBytes > 0 ? $", {gpu.VramBytes / (1024.0 * 1024 * 1024):F0} GB VRAM" : string.Empty;
                HardwareRows.Add(new InfoRow($"GPU ({gpu.Vendor})", $"{gpu.Name}, driver {gpu.DriverVersion}{vram}"));
            }
            HardwareRows.Add(new InfoRow("RAM", $"{inventory.TotalRamBytes / (1024.0 * 1024 * 1024):F0} GB"));
            HardwareRows.Add(new InfoRow("Windows", inventory.WindowsVersion));

            var virtualization = inventory.Virtualization;
            VirtualizationRows.Clear();
            VirtualizationRows.Add(new InfoRow("Firmware virtualization", Tri(virtualization.FirmwareVirtualizationEnabled)));
            VirtualizationRows.Add(new InfoRow("Hypervisor running", Tri(virtualization.HypervisorPresent)));
            VirtualizationRows.Add(new InfoRow("Hyper-V feature", Tri(virtualization.HyperVFeatureEnabled)));
            VirtualizationRows.Add(new InfoRow("Virtual Machine Platform", Tri(virtualization.VirtualMachinePlatformEnabled)));
            VirtualizationRows.Add(new InfoRow("Windows Hypervisor Platform", Tri(virtualization.WindowsHypervisorPlatformEnabled)));

            var overrides = (await _settings.GetSettingsAsync(ct)).DisplayOverrides;
            Displays.Clear();
            foreach (var display in inventory.Displays)
            {
                Displays.Add(new MonitorRow(
                    display.DeviceName,
                    DisplayPresentation.CustomName(display, overrides) ?? display.FriendlyName,
                    display.CurrentMode.ToString()));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string Tri(bool? value) => value switch
    {
        true => "Enabled",
        false => "Disabled",
        null => "Unknown",
    };
}
