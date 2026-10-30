using System.Diagnostics.Eventing.Reader;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Platform.Windows.Services;

/// <summary>
/// Reads Memory Integrity, the hypervisor scheduler and the graphics cards' interrupt mode. Only
/// reads: WMI queries, one event log query and registry keys opened read-only. A source that fails
/// is left unknown and the check says so, rather than guessing.
/// </summary>
public sealed class WindowsHostTuningProbe : IHostTuningProbe
{
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string MsiSubKey = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    private readonly ILogger<WindowsHostTuningProbe> _logger;

    public WindowsHostTuningProbe(ILogger<WindowsHostTuningProbe> logger) => _logger = logger;

    public Task<MemoryIntegrityReading> ReadMemoryIntegrityAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            List<int>? running = null;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
                foreach (var guard in searcher.Get())
                {
                    running = guard["SecurityServicesRunning"] is Array values
                        ? values.Cast<object>().Select(Convert.ToInt32).ToList()
                        : [];
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogDebug(ex, "Device Guard query failed; reading Memory Integrity from the registry only");
            }

            using var key = Registry.LocalMachine.OpenSubKey(HvciKey);
            var enabled = key?.GetValue("Enabled") is int value ? value : (int?)null;
            return new MemoryIntegrityReading(running, enabled);
        }, ct);

    public Task<HypervisorSchedulerReading?> ReadHypervisorSchedulerAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and EventID=2]]")
                {
                    // Newest first, so the first record read is the one that counts.
                    ReverseDirection = true,
                };
                using var reader = new EventLogReader(query);
                using var record = reader.ReadEvent();
                if (record?.Properties.Count > 0 && record.TimeCreated is { } at)
                {
                    return new HypervisorSchedulerReading(Convert.ToInt32(record.Properties[0].Value), at);
                }
            }
            // Access denied on the System log surfaces as UnauthorizedAccessException, and a record
            // whose first value is not a number throws from Convert; both leave the type unknown.
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidCastException or FormatException)
            {
                _logger.LogDebug(ex, "Could not read the hypervisor scheduler type from the System log");
            }
            return (HypervisorSchedulerReading?)null;
        }, ct);

    public Task<IReadOnlyList<GpuInterruptReading>> ReadGpuInterruptsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<GpuInterruptReading>>(() =>
        {
            var adapters = new List<GpuInterruptReading>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
                foreach (var entity in searcher.Get())
                {
                    var id = entity["PNPDeviceID"]?.ToString() ?? string.Empty;
                    // Only a card on the PCI bus raises interrupts; a virtual display is a software device.
                    if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    int? msi;
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}\{MsiSubKey}");
                        msi = key?.GetValue("MSISupported") is int value ? value : null;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                    {
                        // A driver that locks its own key down: left out rather than listed as line-based,
                        // which an unreadable key would otherwise look like.
                        _logger.LogDebug(ex, "Could not read the interrupt mode of {Adapter}", id);
                        continue;
                    }
                    adapters.Add(new GpuInterruptReading(entity["Name"]?.ToString() ?? id, id, msi));
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogDebug(ex, "Display adapter query failed");
            }
            return adapters;
        }, ct);
}
