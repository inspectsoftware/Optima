using Optima.Core.Models;

namespace Optima.Core.Launch;

/// <summary>One power plan as Windows lists it on this PC.</summary>
public sealed record PowerScheme(Guid Id, string Name);

/// <summary>
/// Which listed power plan a profile's choice means on this PC. Only a plan Windows lists can be
/// the answer. A PC with Modern Standby lists Balanced and whatever its vendor added, and hides
/// High performance and Ultimate Performance: their well-known GUIDs still exist there, but asking
/// Windows to activate one fails, and that used to take the whole launch down with it.
/// </summary>
public static class PowerPlanPolicy
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>The one copy of Ultimate Performance Optima makes where Windows hides the built-in plan.</summary>
    public static readonly Guid OptimaUltimatePerformance = new("0b71a3c5-6d2e-4f0a-9c1b-5e8f2a7d4c10");

    /// <summary>
    /// The listed plan for the choice, or null when this PC does not list one. Unchanged has no plan
    /// of its own and is null too.
    /// </summary>
    public static Guid? Resolve(PowerPlanKind kind, IReadOnlyList<PowerScheme> listed) => kind switch
    {
        PowerPlanKind.Balanced => ById(listed, Balanced),
        // A vendor's own high performance plan carries another GUID; its name is all there is to go on.
        PowerPlanKind.HighPerformance => ById(listed, HighPerformance) ?? ByName(listed, "high performance"),
        PowerPlanKind.UltimatePerformance => ById(listed, UltimatePerformance) ?? ById(listed, OptimaUltimatePerformance),
        _ => null,
    };

    /// <summary>
    /// The choice itself, or the nearest plan this PC lists: where Ultimate Performance cannot be
    /// had, High performance is the same intent one step down. Nothing stands in for the others.
    /// </summary>
    public static Guid? ResolveNearest(PowerPlanKind kind, IReadOnlyList<PowerScheme> listed)
        => Resolve(kind, listed)
            ?? (kind == PowerPlanKind.UltimatePerformance ? Resolve(PowerPlanKind.HighPerformance, listed) : null);

    private static Guid? ById(IReadOnlyList<PowerScheme> listed, Guid id)
        => listed.Any(s => s.Id == id) ? id : null;

    private static Guid? ByName(IReadOnlyList<PowerScheme> listed, string fragment)
        => listed.FirstOrDefault(s => s.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))?.Id;
}
