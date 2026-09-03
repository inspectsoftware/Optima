using System.Text.Json.Serialization;

namespace Optima.Core.Models;

/// <summary>Display portion of a launch profile.</summary>
public sealed record DisplayProfile
{
    public bool VirtualDisplay { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int RefreshRate { get; init; } = 60;

    public bool MakePrimary { get; init; }

    [JsonIgnore]
    public DisplayMode Mode => new(Width, Height, RefreshRate);
}

public enum PowerPlanKind
{
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

/// <summary>Performance portion of a launch profile.</summary>
public sealed record PerformanceProfile
{
    public PowerPlanKind PowerPlan { get; init; } = PowerPlanKind.Unchanged;

    public ProcessPriorityLevel Priority { get; init; } = ProcessPriorityLevel.Unchanged;

    public bool DisablePowerThrottling { get; init; }

    public ulong CpuAffinityMask { get; init; }

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
