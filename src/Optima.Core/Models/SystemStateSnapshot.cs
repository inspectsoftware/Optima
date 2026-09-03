namespace Optima.Core.Models;

/// <summary>Everything the bootstrapper changed (or is about to change) so it can be restored later, including after a crash (§18/§19).</summary>
public sealed record SystemStateSnapshot
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProfileName { get; init; } = string.Empty;

    public Guid? PreviousPowerScheme { get; init; }

    public bool VirtualDisplayEnabledByUs { get; init; }

    public bool VirtualDisplayConfigured { get; init; }

    public string? VddSettingsBackupPath { get; init; }

    public string? DisplayTopology { get; init; }

    public string? ChangedDisplayDevice { get; init; }
    public DisplayMode? OriginalDisplayMode { get; init; }

    public IReadOnlyList<ProcessStateSnapshot> ProcessStates { get; init; } = [];
}

/// <summary>Original scheduling state of one process we tuned.</summary>
public sealed record ProcessStateSnapshot
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public ProcessPriorityLevel OriginalPriority { get; init; }
    public ulong OriginalAffinityMask { get; init; }
    public bool PowerThrottlingWasEnabled { get; init; }
}
