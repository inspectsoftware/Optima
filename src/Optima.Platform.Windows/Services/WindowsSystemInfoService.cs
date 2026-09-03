using System.Management;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Optima.Platform.Windows.Services;

/// <summary>Hardware / OS inventory via WMI + display enumeration (§4/§11/§16).</summary>
public sealed class WindowsSystemInfoService : ISystemInfoService
{
    private readonly IDisplayService _displayService;
    private readonly ILogger<WindowsSystemInfoService> _logger;
    private SystemInventory? _cached;
    private Task<VirtualizationState>? _virtualization;

    public WindowsSystemInfoService(IDisplayService displayService, ILogger<WindowsSystemInfoService> logger)
    {
        _displayService = displayService;
        _logger = logger;
    }

    public async Task<SystemInventory> GetInventoryAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached with { Displays = await _displayService.GetDisplaysAsync(ct).ConfigureAwait(false) };
        }

        var inventory = await Task.Run(BuildInventory, ct).ConfigureAwait(false);
        var displays = await _displayService.GetDisplaysAsync(ct).ConfigureAwait(false);
        var virtualization = await GetVirtualizationStateAsync(ct).ConfigureAwait(false);
        _cached = inventory with { Displays = displays, Virtualization = virtualization };
        return _cached;
    }

    public Task<VirtualizationState> GetVirtualizationStateAsync(CancellationToken ct = default)
    {
        var pending = _virtualization;
        if (pending is null || pending.IsFaulted || pending.IsCanceled)
        {
            pending = _virtualization = QueryVirtualizationStateAsync();
        }
        return pending.WaitAsync(ct);
    }

    public void InvalidateCache() => _virtualization = null;

    private Task<VirtualizationState> QueryVirtualizationStateAsync()
        => Task.Run(() =>
        {
            bool? firmware = null;
            bool? hypervisorPresent = null;
            try
            {
                using var cpuSearcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
                foreach (var cpu in cpuSearcher.Get())
                {
                    firmware = cpu["VirtualizationFirmwareEnabled"] as bool?;
                }

                using var csSearcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
                foreach (var cs in csSearcher.Get())
                {
                    hypervisorPresent = cs["HypervisorPresent"] as bool?;
                }
            }
            catch (ManagementException ex)
            {
                _logger.LogWarning(ex, "WMI virtualization query failed");
            }

            if (hypervisorPresent == true)
            {
                firmware = true;
            }

            return new VirtualizationState
            {
                FirmwareVirtualizationEnabled = firmware,
                HypervisorPresent = hypervisorPresent,
                HyperVFeatureEnabled = GetOptionalFeatureEnabled("Microsoft-Hyper-V"),
                VirtualMachinePlatformEnabled = GetOptionalFeatureEnabled("VirtualMachinePlatform"),
                WindowsHypervisorPlatformEnabled = GetOptionalFeatureEnabled("HypervisorPlatform"),
            };
        });

    private SystemInventory BuildInventory()
    {
        string cpuName = string.Empty;
        int cores = 0, threads = 0;
        var gpus = new List<GpuInfo>();
        ulong totalRam = 0;
        string windowsVersion = Environment.OSVersion.VersionString;

        try
        {
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var cpu in cpuSearcher.Get())
            {
                cpuName = cpu["Name"]?.ToString()?.Trim() ?? string.Empty;
                cores += Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
                threads += Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
            }

            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (var gpu in gpuSearcher.Get())
            {
                var name = gpu["Name"]?.ToString() ?? "Unknown GPU";
                gpus.Add(new GpuInfo
                {
                    Name = name,
                    DriverVersion = gpu["DriverVersion"]?.ToString() ?? string.Empty,
                    VramBytes = (ulong)Math.Max(0, Convert.ToInt64(gpu["AdapterRAM"] ?? 0L)),
                    Vendor = ClassifyVendor(name),
                });
            }

            totalRam = ProcessNative.GetMemoryStatus().TotalBytes;

            using var osSearcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (var os in osSearcher.Get())
            {
                var caption = os["Caption"]?.ToString()?.Trim();
                var build = os["BuildNumber"]?.ToString();
                if (caption is not null)
                {
                    windowsVersion = $"{caption} (build {build})";
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogError(ex, "WMI inventory query failed, returning partial inventory");
        }

        return new SystemInventory
        {
            CpuName = cpuName,
            CpuCores = cores,
            CpuThreads = threads,
            Gpus = gpus,
            TotalRamBytes = totalRam,
            WindowsVersion = windowsVersion,
        };
    }

    internal static GpuVendor ClassifyVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }
        return GpuVendor.Unknown;
    }

    private bool? GetOptionalFeatureEnabled(string featureName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT InstallState FROM Win32_OptionalFeature WHERE Name = '{featureName}'");
            foreach (var feature in searcher.Get())
            {
                return Convert.ToInt32(feature["InstallState"] ?? 0) == 1;
            }
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogDebug(ex, "Optional feature query failed for {Feature}", featureName);
            return null;
        }
    }
}
