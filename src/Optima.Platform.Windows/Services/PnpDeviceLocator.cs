using System.Management;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Finds PnP device instance ids (used to pass display devices to the elevated helper).</summary>
public sealed class PnpDeviceLocator
{
    private readonly ILogger<PnpDeviceLocator> _logger;

    public PnpDeviceLocator(ILogger<PnpDeviceLocator> logger)
    {
        _logger = logger;
    }

    public sealed record PnpDevice(string InstanceId, string Name, bool Enabled);

    public Task<IReadOnlyList<PnpDevice>> FindDisplayDevicesAsync(string nameContains, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PnpDevice>>(() =>
        {
            var devices = new List<PnpDevice>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Name, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
                foreach (var entity in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entity["Name"]?.ToString() ?? string.Empty;
                    var id = entity["PNPDeviceID"]?.ToString() ?? string.Empty;
                    if (id.Length == 0 || !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var errorCode = Convert.ToInt32(entity["ConfigManagerErrorCode"] ?? 0);
                    devices.Add(new PnpDevice(id, name, Enabled: errorCode != 22));
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogError(ex, "PnP device query failed for '{Name}'", nameContains);
            }
            return devices;
        }, ct);
}
