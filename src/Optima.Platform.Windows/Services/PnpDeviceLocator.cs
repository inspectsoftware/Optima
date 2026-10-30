using System.Management;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Finds PnP device instance ids (used to pass display devices to the elevated helper).</summary>
public sealed class PnpDeviceLocator
{
    /// <summary>The hardware id the virtual display's root node is created with.</summary>
    public const string VddHardwareId = @"Root\MttVDD";

    /// <summary>Device Manager's "disabled" code: the only problem that is a choice rather than a fault.</summary>
    public const int ProblemDisabled = 22;

    private readonly ILogger<PnpDeviceLocator> _logger;

    public PnpDeviceLocator(ILogger<PnpDeviceLocator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// One device node. <paramref name="ProblemCode"/> is Device Manager's code (0 = working);
    /// <see cref="Enabled"/> only means "not switched off": a node can be enabled and still broken.
    /// </summary>
    public sealed record PnpDevice(string InstanceId, string Name, bool Enabled, int ProblemCode = 0)
    {
        public bool Healthy => ProblemCode == 0;
    }

    /// <summary>
    /// Every display node whose name contains <paramref name="nameContains"/> or whose hardware id is
    /// <see cref="VddHardwareId"/>. The hardware id matters because a node without a bound driver
    /// (code 28) has no friendly name, and would otherwise be invisible while it still blocks a reinstall.
    /// Healthiest first, so a caller taking the first gets the best node.
    /// </summary>
    public Task<IReadOnlyList<PnpDevice>> FindDisplayDevicesAsync(string nameContains, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PnpDevice>>(() =>
        {
            var devices = new List<PnpDevice>();
            try
            {
                // Root-enumerated nodes are included whatever their class: a failed install can leave one classless.
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Name, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE PNPClass = 'Display' OR PNPDeviceID LIKE 'ROOT\\\\%'");
                foreach (var entity in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entity["Name"]?.ToString() ?? string.Empty;
                    var id = entity["PNPDeviceID"]?.ToString() ?? string.Empty;
                    if (id.Length == 0 || !Matches(name, entity["HardwareID"] as string[], nameContains))
                    {
                        continue;
                    }
                    var errorCode = Convert.ToInt32(entity["ConfigManagerErrorCode"] ?? 0);
                    _logger.LogInformation("Virtual display node {Id} '{Name}': problem code {Code}", id, name, errorCode);
                    devices.Add(new PnpDevice(id, name, Enabled: errorCode != ProblemDisabled, errorCode));
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                _logger.LogError(ex, "PnP device query failed for '{Name}'", nameContains);
            }
            return HealthiestFirst(devices);
        }, ct);

    /// <summary>The name contains the marker, or one of the hardware ids is <see cref="VddHardwareId"/> (case-insensitive).</summary>
    public static bool Matches(string name, IReadOnlyList<string>? hardwareIds, string nameContains)
        => (nameContains.Length > 0 && name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
           || (hardwareIds?.Any(h => string.Equals(h, VddHardwareId, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>Working nodes, then disabled ones (one switch from working), then broken ones; stable within each.</summary>
    public static IReadOnlyList<PnpDevice> HealthiestFirst(IEnumerable<PnpDevice> devices)
        => devices.OrderBy(d => d.ProblemCode switch { 0 => 0, ProblemDisabled => 1, _ => 2 }).ToList();
}
