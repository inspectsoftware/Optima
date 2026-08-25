namespace Optima.Core.Models;

/// <summary>
/// Everything the bootstrapper changed (or is about to change) so it can be restored later,
/// including after a crash (§18/§19). Persisted as JSON before any system mutation.
/// </summary>
public sealed record SystemStateSnapshot
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Name of the profile the session was started with (informational).</summary>
    public string ProfileName { get; init; } = string.Empty;

    /// <summary>Active power scheme GUID before we changed it; null = not changed.</summary>
    public Guid? PreviousPowerScheme { get; init; }

    /// <summary>Whether the virtual display was enabled by us (and must be disabled on restore).</summary>
    public bool VirtualDisplayEnabledByUs { get; init; }

    /// <summary>True when the session touched the virtual display at all (mode set, driver settings edit).</summary>
    public bool VirtualDisplayConfigured { get; init; }

    /// <summary>Backup path of the driver settings file we rewrote; null = not touched.</summary>
    public string? VddSettingsBackupPath { get; init; }

    /// <summary>Opaque serialized display topology captured before display changes; null = not changed.</summary>
    public string? DisplayTopology { get; init; }

    /// <summary>Device name of the display whose mode we changed, with its original mode.</summary>
    public string? ChangedDisplayDevice { get; init; }
    public DisplayMode? OriginalDisplayMode { get; init; }

    /// <summary>Process tweaks that were applied (restored best-effort; processes may have exited).</summary>
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
