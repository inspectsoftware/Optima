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
