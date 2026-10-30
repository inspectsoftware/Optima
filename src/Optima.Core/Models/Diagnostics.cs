namespace Optima.Core.Models;

public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail,
    Skipped,
}

/// <summary>Outcome of one diagnostics check (§15).</summary>
public sealed record DiagnosticResult
{
    public required string CheckName { get; init; }
    public required DiagnosticStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string RecommendedFix { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;

    /// <summary>
    /// The issue this result stands for when it is not a pass, by its error guide code. Null means
    /// the result is advice: it shows on the Checks tab and never becomes an issue. Most warnings
    /// are advice (a 60 Hz monitor is not a fault), and an issue list that never empties stops
    /// being read.
    /// </summary>
    public string? IssueCode { get; init; }
}

/// <summary>Virtualization facts used by diagnostics (§16).</summary>
public sealed record VirtualizationState
{
    public bool? FirmwareVirtualizationEnabled { get; init; }
    public bool? HypervisorPresent { get; init; }
    public bool? HyperVFeatureEnabled { get; init; }
    public bool? VirtualMachinePlatformEnabled { get; init; }
    public bool? WindowsHypervisorPlatformEnabled { get; init; }
    public string HypervisorLaunchType { get; init; } = string.Empty;
}

/// <summary>
/// What Windows says about Memory Integrity (hypervisor-protected code integrity), as read, so the
/// check decides and a test can hand it any combination. Null is a source that could not be read.
/// </summary>
/// <param name="SecurityServicesRunning">Win32_DeviceGuard's SecurityServicesRunning; 2 is Memory Integrity.</param>
/// <param name="RegistryEnabled">The Enabled value under DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity.</param>
public sealed record MemoryIntegrityReading(IReadOnlyList<int>? SecurityServicesRunning, int? RegistryEnabled);

/// <summary>The scheduler type the hypervisor logged when it last started (event 2), and when.</summary>
public sealed record HypervisorSchedulerReading(int Type, DateTimeOffset LoggedAt);

/// <summary>One graphics adapter and its MSISupported value; null when the adapter has none set.</summary>
public sealed record GpuInterruptReading(string Name, string InstanceId, int? MsiSupported);
