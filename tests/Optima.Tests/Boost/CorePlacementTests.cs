using Optima.Core.Boost;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class CorePlacementTests
{
    private const long Mb = 1 << 20;

    /// <summary>Two threads per core; ranks[i] is core i's place in Windows' ranking.</summary>
    private static List<LogicalCpu> Cores(int[] ranks, byte die = 0, long l3 = 16 * Mb, byte efficiency = 0, int firstCore = 0)
        => [.. ranks.SelectMany((rank, i) => Enumerable.Range(0, 2).Select(t =>
        {
            var logical = (firstCore + i) * 2 + t;
            return new LogicalCpu((uint)(256 + logical), 0, (byte)logical, (byte)((firstCore + i) * 2), die, efficiency, (byte)rank, false, l3);
        }))];

    [Fact]
    public void OnOneDie_TheCoreOfProcessorZeroAndTheSlowest_AreLeftOut()
    {
        // The 7735HS this was written on: cores 3 and 5 rank highest, core 7 lowest.
        var plan = CorePlacement.Choose(Cores([4, 1, 5, 6, 3, 6, 2, 0]))!;

        Assert.Equal(6, plan.Cores);
        Assert.Equal(8, plan.TotalCores);
        Assert.True(plan.Narrows);
        Assert.DoesNotContain(0, plan.LogicalProcessors);
        Assert.DoesNotContain(14, plan.LogicalProcessors);
        Assert.Contains(6, plan.LogicalProcessors);
        Assert.Contains(7, plan.LogicalProcessors);
        Assert.Equal([256u, 257u, 270u, 271u], plan.SpareCpuSetIds);
    }

    [Fact]
    public void BothThreadsOfACore_GoTogether()
    {
        var plan = CorePlacement.Choose(Cores([4, 1, 5, 6, 3, 6, 2, 0]))!;

        Assert.All(plan.LogicalProcessors, l => Assert.Contains(l ^ 1, plan.LogicalProcessors));
    }

    [Fact]
    public void SixCoresOrFewer_AreAllKept()
    {
        var plan = CorePlacement.Choose(Cores([0, 1, 2, 3, 4, 5]))!;

        Assert.False(plan.Narrows);
        Assert.Equal(12, plan.CpuSetIds.Count);
    }

    [Fact]
    public void OnAHybridProcessor_OnlyPerformanceCoresAreUsed()
    {
        var cpus = Cores([1, 1, 1, 1, 1, 1, 1, 1], efficiency: 1).Concat(Cores([0, 0, 0, 0], efficiency: 0, firstCore: 8)).ToList();

        var plan = CorePlacement.Choose(cpus)!;

        Assert.All(plan.LogicalProcessors, l => Assert.True(l < 16));
        Assert.Contains("performance cores", plan.Reason);
    }

    [Fact]
    public void ALaptopWithTwoPerformanceCores_KeepsItsEfficiencyCores()
    {
        // An i5-1235U: two performance cores with two threads each, eight efficiency cores.
        var cpus = Cores([5, 5], efficiency: 1)
            .Concat(Enumerable.Range(0, 8).Select(i => new LogicalCpu((uint)(260 + i), 0, (byte)(4 + i), (byte)(4 + i), 0, 0, 1, false, 12 * Mb)))
            .ToList();

        var plan = CorePlacement.Choose(cpus)!;

        Assert.DoesNotContain("performance cores", plan.Reason);
        Assert.True(plan.Cores >= CorePlacement.MinCores);
    }

    [Fact]
    public void OnADualDieX3d_TheDieWithTheLargerCacheIsUsed_EvenWhenTheOtherClocksHigher()
    {
        var cpus = Cores([1, 1, 1, 1, 1, 1, 1, 1], die: 0, l3: 96 * Mb)
            .Concat(Cores([9, 9, 9, 9, 9, 9, 9, 9], die: 1, l3: 32 * Mb, firstCore: 8)).ToList();

        var plan = CorePlacement.Choose(cpus)!;

        Assert.All(plan.LogicalProcessors, l => Assert.True(l < 16));
        Assert.Contains("larger cache", plan.Reason);
        Assert.Equal(16, plan.TotalCores);
    }

    [Fact]
    public void OnTwoAlikeDies_TheOneWithTheFastestCoreIsUsed()
    {
        var cpus = Cores([1, 1, 1, 1, 1, 1, 1, 1], die: 0)
            .Concat(Cores([2, 2, 2, 2, 2, 2, 2, 2], die: 1, firstCore: 8)).ToList();

        var plan = CorePlacement.Choose(cpus)!;

        Assert.All(plan.LogicalProcessors, l => Assert.True(l >= 16));
    }

    [Fact]
    public void ADieTooSmallForTheGame_IsNotNarrowedTo()
    {
        var cpus = Cores([1, 1, 1, 1], die: 0, l3: 96 * Mb)
            .Concat(Cores([1, 1, 1, 1], die: 1, l3: 32 * Mb, firstCore: 4)).ToList();

        var plan = CorePlacement.Choose(cpus)!;

        Assert.Equal(6, plan.Cores);
        Assert.DoesNotContain("cache", plan.Reason);
    }

    [Fact]
    public void ReservedProcessors_AreNeverChosen()
    {
        var cpus = Cores([4, 1, 5, 6, 3, 6, 2, 0]);
        cpus[6] = cpus[6] with { Reserved = true };

        var plan = CorePlacement.Choose(cpus)!;

        Assert.DoesNotContain(cpus[6].Id, plan.CpuSetIds);
    }

    [Fact]
    public void MoreThanOneProcessorGroup_IsNotPlaced()
    {
        var cpus = Cores([1, 1, 1, 1, 1, 1, 1, 1]);
        cpus[15] = cpus[15] with { Group = 1 };

        Assert.Null(CorePlacement.Choose(cpus));
    }
}
