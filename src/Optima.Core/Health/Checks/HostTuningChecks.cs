using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Core.Health.Checks;

/// <summary>
/// Whether Windows' Memory Integrity is on. It is a security feature, not a fault: it often costs
/// some frame rate, and whether that is worth the protection is the player's call. So the check says
/// both sides and lists it as a note, and Optima never touches the setting.
/// </summary>
public sealed class MemoryIntegrityCheck : IDiagnosticCheck
{
    /// <summary>The SecurityServicesRunning value that stands for Memory Integrity (hypervisor-enforced code integrity).</summary>
    public const int HvciService = 2;

    private readonly IHostTuningProbe _probe;
    public MemoryIntegrityCheck(IHostTuningProbe probe) => _probe = probe;

    public string Name => "Memory Integrity";
    public int Order => 22;
    public CheckScope Scope => CheckScope.Startup;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var reading = await _probe.ReadMemoryIntegrityAsync(ct).ConfigureAwait(false);
        var running = reading.SecurityServicesRunning?.Contains(HvciService);
        var details = "Win32_DeviceGuard SecurityServicesRunning: "
            + (reading.SecurityServicesRunning is { } services ? "{" + string.Join(", ", services) + "}" : "could not be read")
            + "\nDeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity Enabled: "
            + (reading.RegistryEnabled?.ToString() ?? "not set");

        if (running is null && reading.RegistryEnabled is null)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Skipped,
                Reason = "Windows did not say whether Memory Integrity is on.",
                Details = details,
            };
        }

        // Device Guard was asked and says it is not running, so it costs nothing now even when the
        // setting is on: either it starts with the next restart, or this PC cannot run it (virtualization
        // off in the firmware, or a driver it blocks). Either way there is no cost to note today.
        if (running == false && reading.RegistryEnabled == 1)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Pass,
                Reason = "Memory Integrity is switched on but not running, so it costs no frame rate now. "
                    + "It starts after a restart, unless this PC cannot run it.",
                Details = details,
                IssueCode = "MEMORY_INTEGRITY_ON",
            };
        }

        if (running == true || reading.RegistryEnabled == 1)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Warning,
                Reason = running == true
                    ? "Memory Integrity is on. It stops drivers that were tampered with from loading, and it often costs some frame rate."
                    // Device Guard could not be asked, so the setting is all there is to go on.
                    : "Memory Integrity is switched on and starts with Windows. It stops drivers that were tampered with from loading, and it often costs some frame rate.",
                RecommendedFix = "Nothing is wrong. Keeping it is your choice: Windows Security > Device security > Core isolation turns it off or on, "
                    + "with a restart. Optima never changes it.",
                Details = details,
                IssueCode = "MEMORY_INTEGRITY_ON",
            };
        }

        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Pass,
            Reason = "Memory Integrity is off.",
            Details = details,
            IssueCode = "MEMORY_INTEGRITY_ON",
        };
    }
}

/// <summary>
/// Which scheduler the hypervisor started with. Client Windows uses the root scheduler, where every
/// virtual processor of the game's emulator is an ordinary Windows thread that Windows places, and
/// that is what lets core placement reach the game. Under the classic or core scheduler the
/// hypervisor places them itself, out of Windows' reach.
/// </summary>
public sealed class HypervisorSchedulerCheck : IDiagnosticCheck
{
    public const int RootScheduler = 4;

    private readonly IHostTuningProbe _probe;
    public HypervisorSchedulerCheck(IHostTuningProbe probe) => _probe = probe;

    public string Name => "Hypervisor Scheduler";
    public int Order => 24;
    public CheckScope Scope => CheckScope.Startup;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var reading = await _probe.ReadHypervisorSchedulerAsync(ct).ConfigureAwait(false);
        if (reading is null)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Skipped,
                Reason = "Windows' System log holds no scheduler type from the hypervisor. It is written each time the hypervisor starts.",
            };
        }

        var details = $"System log, Microsoft-Windows-Hyper-V-Hypervisor event 2 at {reading.LoggedAt.ToLocalTime():yyyy-MM-dd HH:mm}: "
            + $"scheduler type 0x{reading.Type:X}";
        if (reading.Type == RootScheduler)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Pass,
                Reason = "The hypervisor uses the root scheduler, so core placement reaches the game's virtual processors.",
                Details = details,
                IssueCode = "HYPERVISOR_SCHEDULER_NOT_ROOT",
            };
        }

        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Warning,
            Reason = $"The hypervisor uses the {Describe(reading.Type)} scheduler, so it places the game's virtual processors itself "
                + "and core placement may not reach the game.",
            RecommendedFix = "Nothing needs repairing. A Hyper-V or server setup usually chose it; client Windows is built for the root scheduler.",
            Details = details,
            IssueCode = "HYPERVISOR_SCHEDULER_NOT_ROOT",
        };
    }

    public static string Describe(int type) => type switch
    {
        1 => "classic (SMT off)",
        2 => "classic",
        3 => "core",
        RootScheduler => "root",
        _ => $"unknown (0x{type:X})",
    };
}

/// <summary>
/// How each graphics card raises its interrupts: message-signaled (MSI) or the older line-based way.
/// Shown for information only. The driver picks the mode, and changing it by hand needs a restart and
/// can stop the card from starting, so there is no fix to offer and no issue to raise.
/// </summary>
public sealed class GpuInterruptModeCheck : IDiagnosticCheck
{
    private readonly IHostTuningProbe _probe;
    public GpuInterruptModeCheck(IHostTuningProbe probe) => _probe = probe;

    public string Name => "GPU Interrupt Mode";
    public int Order => 75;

    public async Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var adapters = await _probe.ReadGpuInterruptsAsync(ct).ConfigureAwait(false);
        if (adapters.Count == 0)
        {
            return new DiagnosticResult
            {
                CheckName = Name,
                Status = DiagnosticStatus.Skipped,
                Reason = "No graphics card was found to read.",
            };
        }

        var lineBased = adapters.Where(a => a.MsiSupported != 1).Select(a => a.Name).ToList();
        return new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Pass,
            Reason = lineBased.Count == 0
                ? "Every graphics card uses message-signaled interrupts (MSI)."
                : $"Line-based interrupts: {string.Join(", ", lineBased)}. For information only: the driver chooses the mode, "
                    + "and changing it by hand needs a restart and can stop the card from starting, so Optima leaves it alone.",
            Details = string.Join("\n", adapters.Select(a => $"{a.Name}: {Describe(a.MsiSupported)} ({a.InstanceId})")),
        };
    }

    public static string Describe(int? msiSupported) => msiSupported switch
    {
        1 => "MSI on (MSISupported = 1)",
        null => "line-based (MSISupported not set)",
        _ => $"line-based (MSISupported = {msiSupported})",
    };
}
