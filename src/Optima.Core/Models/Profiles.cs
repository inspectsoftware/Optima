using System.Text.Json.Serialization;

namespace Optima.Core.Models;

/// <summary>Display portion of a launch profile.</summary>
public sealed record DisplayProfile
{
    public bool VirtualDisplay { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int RefreshRate { get; init; } = 60;

    /// <summary>When true the virtual display is made the primary display for the session.</summary>
    public bool MakePrimary { get; init; }

    [JsonIgnore]
    public DisplayMode Mode => new(Width, Height, RefreshRate);
}

public enum PowerPlanKind
{
    /// <summary>Leave the active power plan untouched.</summary>
    Unchanged,
    Balanced,
    HighPerformance,
    UltimatePerformance,
}

public enum ProcessPriorityLevel
{
    Unchanged,
    Normal,
    AboveNormal,
    High,
}

/// <summary>Performance portion of a launch profile. Every field is opt-in and reversible.</summary>
public sealed record PerformanceProfile
{
    public PowerPlanKind PowerPlan { get; init; } = PowerPlanKind.Unchanged;

    /// <summary>Priority applied to the game / emulator processes.</summary>
    public ProcessPriorityLevel Priority { get; init; } = ProcessPriorityLevel.Unchanged;

    /// <summary>Disable Windows power throttling (EcoQoS) for game processes.</summary>
    public bool DisablePowerThrottling { get; init; }

    /// <summary>Optional CPU affinity mask for the emulator process. 0 = unchanged.</summary>
    public ulong CpuAffinityMask { get; init; }

    /// <summary>Names of processes the user opted-in to close before launch (background cleanup, §10).</summary>
    public IReadOnlyList<string> CleanupProcessNames { get; init; } = [];
}

/// <summary>A complete, user-selectable configuration profile (§22).</summary>
public sealed record LaunchProfile
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public DisplayProfile Display { get; init; } = new();
    public PerformanceProfile Performance { get; init; } = new();
    public bool IsBuiltIn { get; init; }
}
