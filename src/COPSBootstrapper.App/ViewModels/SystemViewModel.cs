using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

/// <summary>SYSTEM page (§4/§11/§16): hardware inventory + virtualization facts.</summary>
public sealed partial class SystemViewModel : ObservableObject
{
    private readonly ISystemInfoService _systemInfo;

    public SystemViewModel(ISystemInfoService systemInfo)
    {
        _systemInfo = systemInfo;
    }

    public ObservableCollection<InfoRow> HardwareRows { get; } = [];
    public ObservableCollection<InfoRow> VirtualizationRows { get; } = [];
    public ObservableCollection<DisplayInfo> Displays { get; } = [];

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
                HardwareRows.Add(new InfoRow($"GPU ({gpu.Vendor})", $"{gpu.Name} — driver {gpu.DriverVersion}{vram}"));
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

            Displays.Clear();
            foreach (var display in inventory.Displays)
            {
                Displays.Add(display);
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
