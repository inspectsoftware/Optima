namespace Optima.Core.Models;

public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail,
    Skipped,
}

/// <summary>Outcome of one diagnostics check (§15). Always carries a reason and a recommended fix.</summary>
public sealed record DiagnosticResult
{
    public required string CheckName { get; init; }
    public required DiagnosticStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string RecommendedFix { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

/// <summary>Virtualization facts used by diagnostics (§16). Nulls mean "could not determine".</summary>
public sealed record VirtualizationState
{
    public bool? FirmwareVirtualizationEnabled { get; init; }
    public bool? HypervisorPresent { get; init; }
    public bool? HyperVFeatureEnabled { get; init; }
    public bool? VirtualMachinePlatformEnabled { get; init; }
    public bool? WindowsHypervisorPlatformEnabled { get; init; }
    public string HypervisorLaunchType { get; init; } = string.Empty;
}
