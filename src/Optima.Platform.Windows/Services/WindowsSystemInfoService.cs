using System.Management;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Hardware / OS inventory via WMI + display enumeration (§4/§11/§16).</summary>
public sealed class WindowsSystemInfoService : ISystemInfoService
{
    private readonly IDisplayService _displayService;
    private readonly ILogger<WindowsSystemInfoService> _logger;
    private Task<SystemInventory>? _inventory;
    private Task<VirtualizationState>? _virtualization;

    public WindowsSystemInfoService(IDisplayService displayService, ILogger<WindowsSystemInfoService> logger)
    {
        _displayService = displayService;
        _logger = logger;
    }

    public async Task<SystemInventory> GetInventoryAsync(CancellationToken ct = default)
    {
        // The WMI-heavy part (processor, graphics cards, Windows) cannot change while the app
        // runs: it is built once, and callers that arrive while it is being built share that one
        // build instead of each starting their own. Displays and virtualization can change (a
        // monitor is plugged in, a feature is enabled), so those are asked for every time.
        var inventoryTask = _inventory;
        if (inventoryTask is null || inventoryTask.IsFaulted || inventoryTask.IsCanceled)
        {
            inventoryTask = _inventory = Task.Run(BuildInventory);
        }

        // Three independent probes: a cold inventory costs the sum of them if run in sequence.
        var displaysTask = _displayService.GetDisplaysAsync(ct);
        var virtualizationTask = GetVirtualizationStateAsync(ct);
        await Task.WhenAll(inventoryTask.WaitAsync(ct), displaysTask, virtualizationTask).ConfigureAwait(false);

        return inventoryTask.Result with
        {
            Displays = displaysTask.Result,
            Virtualization = virtualizationTask.Result,
        };
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

    /// <summary>Drops the memoized virtualization probe; the next reader re-queries Windows.</summary>
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
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogWarning(ex, "WMI virtualization query failed");
            }

            if (hypervisorPresent == true)
            {
                firmware = true;
            }

            var features = QueryOptionalFeatures();
            return new VirtualizationState
            {
                FirmwareVirtualizationEnabled = firmware,
                HypervisorPresent = hypervisorPresent,
                HyperVFeatureEnabled = features["Microsoft-Hyper-V"],
                VirtualMachinePlatformEnabled = features["VirtualMachinePlatform"],
                WindowsHypervisorPlatformEnabled = features["HypervisorPlatform"],
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

    private static readonly string[] OptionalFeatures =
    [
        "Microsoft-Hyper-V",
        "VirtualMachinePlatform",
        "HypervisorPlatform",
    ];

    /// <summary>
    /// One Win32_OptionalFeature sweep for every feature we care about. Three separate queries
    /// meant three WMI round trips; a cold WMI connection costs hundreds of milliseconds each.
    /// </summary>
    private IReadOnlyDictionary<string, bool?> QueryOptionalFeatures()
    {
        var states = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in OptionalFeatures)
        {
            states[feature] = null;
        }

        try
        {
            var where = string.Join(" OR ", OptionalFeatures.Select(f => $"Name = '{f}'"));
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, InstallState FROM Win32_OptionalFeature WHERE {where}");
            foreach (var feature in searcher.Get())
            {
                var name = feature["Name"]?.ToString();
                if (name is not null && states.ContainsKey(name))
                {
                    states[name] = Convert.ToInt32(feature["InstallState"] ?? 0) == 1;
                }
            }

            // The sweep succeeded, so a missing row means the feature does not exist here.
            foreach (var feature in OptionalFeatures)
            {
                states[feature] ??= false;
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogDebug(ex, "Optional feature query failed; leaving the features unknown");
        }

        return states;
    }
}
