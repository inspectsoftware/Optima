using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Launch;

public sealed class PowerPlanPolicyTests
{
    private static readonly PowerScheme Balanced = new(PowerPlanPolicy.Balanced, "Balanced");
    private static readonly PowerScheme HighPerformance = new(PowerPlanPolicy.HighPerformance, "High performance");
    private static readonly PowerScheme Ultimate = new(PowerPlanPolicy.UltimatePerformance, "Ultimate Performance");
    private static readonly PowerScheme OptimaUltimate = new(PowerPlanPolicy.OptimaUltimatePerformance, "Ultimate Performance");
    private static readonly PowerScheme PowerSaver = new(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), "Power saver");

    /// <summary>
    /// What powercfg /list shows on a Modern Standby PC with AMD's chipset driver: Balanced and the
    /// vendor's plans, and neither performance plan. This is the list the launch failure came from.
    /// </summary>
    private static readonly PowerScheme[] ModernStandbyPc =
    [
        Balanced,
        new(new Guid("b49a7738-deec-4966-bf89-d8a211f55880"), "Ryzen Optimized Power Saver"),
        new(new Guid("bf08f633-2dba-4924-aae5-4a2e5a70bd29"), "Ryzen Optimized Balanced"),
    ];

    private static readonly PowerScheme[] DesktopPc = [Balanced, HighPerformance, PowerSaver];

    [Theory]
    [InlineData(PowerPlanKind.HighPerformance)]
    [InlineData(PowerPlanKind.UltimatePerformance)]
    public void AModernStandbyPcHasNoPerformancePlanToOffer(PowerPlanKind kind)
    {
        Assert.Null(PowerPlanPolicy.Resolve(kind, ModernStandbyPc));
        Assert.Null(PowerPlanPolicy.ResolveNearest(kind, ModernStandbyPc));
    }

    [Fact]
    public void BalancedIsOfferedEverywhere()
    {
        Assert.Equal(PowerPlanPolicy.Balanced, PowerPlanPolicy.Resolve(PowerPlanKind.Balanced, ModernStandbyPc));
        Assert.Equal(PowerPlanPolicy.Balanced, PowerPlanPolicy.Resolve(PowerPlanKind.Balanced, DesktopPc));
    }

    [Fact]
    public void BalancedIsNeverAVendorPlanThatOnlySharesTheWord()
    {
        PowerScheme[] listed = [ModernStandbyPc[1], ModernStandbyPc[2]];

        Assert.Null(PowerPlanPolicy.Resolve(PowerPlanKind.Balanced, listed));
    }

    [Fact]
    public void TheWellKnownPlanIsChosenWhereWindowsListsIt()
        => Assert.Equal(PowerPlanPolicy.HighPerformance, PowerPlanPolicy.Resolve(PowerPlanKind.HighPerformance, DesktopPc));

    [Fact]
    public void AVendorsOwnHighPerformancePlanIsFoundByItsName()
    {
        var vendor = new PowerScheme(Guid.NewGuid(), "Dell High Performance");

        Assert.Equal(vendor.Id, PowerPlanPolicy.Resolve(PowerPlanKind.HighPerformance, [Balanced, vendor]));
    }

    [Fact]
    public void UltimatePerformanceIsTheBuiltInPlanBeforeOptimasCopy()
    {
        Assert.Equal(PowerPlanPolicy.UltimatePerformance,
            PowerPlanPolicy.Resolve(PowerPlanKind.UltimatePerformance, [Balanced, OptimaUltimate, Ultimate]));
        Assert.Equal(PowerPlanPolicy.OptimaUltimatePerformance,
            PowerPlanPolicy.Resolve(PowerPlanKind.UltimatePerformance, [Balanced, OptimaUltimate]));
    }

    [Fact]
    public void WhereUltimateCannotBeHadHighPerformanceStandsIn()
    {
        Assert.Null(PowerPlanPolicy.Resolve(PowerPlanKind.UltimatePerformance, DesktopPc));
        Assert.Equal(PowerPlanPolicy.HighPerformance, PowerPlanPolicy.ResolveNearest(PowerPlanKind.UltimatePerformance, DesktopPc));
    }

    [Fact]
    public void NothingStandsInForHighPerformance()
        => Assert.Null(PowerPlanPolicy.ResolveNearest(PowerPlanKind.HighPerformance, [Balanced, Ultimate]));

    [Fact]
    public void UnchangedNeverNamesAPlan()
    {
        Assert.Null(PowerPlanPolicy.Resolve(PowerPlanKind.Unchanged, DesktopPc));
        Assert.Null(PowerPlanPolicy.ResolveNearest(PowerPlanKind.Unchanged, DesktopPc));
    }

    public static TheoryData<PowerPlanKind, PowerScheme[]> EveryChoiceOnEveryPc()
    {
        var data = new TheoryData<PowerPlanKind, PowerScheme[]>();
        PowerScheme[][] pcs = [ModernStandbyPc, DesktopPc, [Balanced, Ultimate], [Balanced, OptimaUltimate], []];
        foreach (var kind in Enum.GetValues<PowerPlanKind>())
        {
            foreach (var pc in pcs)
            {
                data.Add(kind, pc);
            }
        }
        return data;
    }

    /// <summary>The rule the launch failure broke: a plan Windows does not list is never the answer.</summary>
    [Theory]
    [MemberData(nameof(EveryChoiceOnEveryPc))]
    public void TheAnswerIsAlwaysAPlanWindowsLists(PowerPlanKind kind, PowerScheme[] listed)
    {
        foreach (var answer in new[] { PowerPlanPolicy.Resolve(kind, listed), PowerPlanPolicy.ResolveNearest(kind, listed) })
        {
            Assert.True(answer is null || listed.Any(s => s.Id == answer), $"{kind} resolved to an unlisted plan");
        }
    }
}
