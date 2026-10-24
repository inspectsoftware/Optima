using Optima.Core.Boost;
using Xunit;

namespace Optima.Tests.Boost;

public sealed class BoostReadoutTests
{
    [Theory]
    [InlineData(1000, 1000, 8, 12.5)]  // one busy thread on eight logical processors
    [InlineData(4000, 1000, 4, 100)]   // every processor busy
    [InlineData(250, 500, 2, 25)]
    [InlineData(9000, 1000, 4, 100)]   // a late sample cannot claim more than the machine
    [InlineData(-50, 1000, 4, 0)]      // a counter that went backwards is not negative load
    [InlineData(100, 0, 4, 0)]         // no time passed, nothing to divide by
    [InlineData(100, 1000, 0, 0)]
    public void CpuShareIsProcessorTimeOverElapsedTimesProcessors(int cpuMs, int elapsedMs, int processors, double expected)
        => Assert.Equal(expected, BoostReadout.CpuPercent(TimeSpan.FromMilliseconds(cpuMs), TimeSpan.FromMilliseconds(elapsedMs), processors), 6);

    [Theory]
    [InlineData(1.0, 15.625)]
    [InlineData(0.5, 31.25)]
    [InlineData(15.625, 1)]
    [InlineData(0, 1)]
    public void TimerFactorIsAgainstWindowsDefaultTick(double ms, double expected)
        => Assert.Equal(expected, BoostReadout.FinerBy(ms), 6);

    [Theory]
    [InlineData(false, 1.0, 0.5, false, 15.625)] // another program's 0.5 ms is not Optima's tick
    [InlineData(false, 1.0, 0.5, true, 0.5)]     // where every program gets the finest request, it is
    [InlineData(true, 1.0, 0.5, false, 1.0)]     // holding, it is what Optima asked for, not the clock
    [InlineData(true, 1.0, 0.5, true, 1.0)]
    [InlineData(false, 1.0, 0, true, 15.625)]    // the clock not read yet
    public void TheTimerTickShownIsOptimasOwn_NotWindowsClock(bool holding, double requestedMs, double clockMs, bool reachesEveryProgram, double expected)
        => Assert.Equal(expected, BoostReadout.TimerTickMs(holding, requestedMs, clockMs, reachesEveryProgram), 6);

    [Fact]
    public void ProcessorSharesAddUpToTheWholeMachine()
    {
        var shares = BoostReadout.SplitProcessor(busyPercent: 60, gamePercent: 35, heldBackPercent: 5);

        Assert.Equal(new ProcessorShares(35, 5, 20, 40), shares);
    }

    [Fact]
    public void ProcessorShares_ReadingsThatDisagree_AreClampedRatherThanOverflowing()
    {
        // The game's sample says more than the whole-machine sample taken a moment apart.
        var shares = BoostReadout.SplitProcessor(busyPercent: 30, gamePercent: 34, heldBackPercent: 3);

        Assert.Equal(new ProcessorShares(30, 0, 0, 70), shares);
        Assert.Equal(100, shares.Game + shares.HeldBack + shares.Other + shares.Idle, 6);
    }

    [Fact]
    public void MemoryShares_WithTheHelpersReadout_SplitStandbyFreedAndFree()
    {
        const ulong Gb = 1024UL * 1024 * 1024;
        var shares = BoostReadout.SplitMemory(totalBytes: 16 * Gb, usedBytes: 8 * Gb, gameBytes: 2 * Gb, standbyMb: 4096, freedMb: 1024);

        Assert.Equal(12.5, shares.Game, 6);
        Assert.Equal(37.5, shares.Other, 6);
        Assert.Equal(25, shares.Standby, 6);
        Assert.Equal(6.25, shares.Freed, 6);
        Assert.Equal(18.75, shares.Free, 6);
    }

    [Fact]
    public void MemoryShares_WithoutTheReadout_EverythingNotInUseIsFree_AndFreedNeverExceedsIt()
    {
        const ulong Gb = 1024UL * 1024 * 1024;
        var shares = BoostReadout.SplitMemory(totalBytes: 8 * Gb, usedBytes: 7 * Gb, gameBytes: 9 * Gb, standbyMb: null, freedMb: 4096);

        Assert.Equal(87.5, shares.Game, 6);
        Assert.Equal(0, shares.Other, 6);
        Assert.Equal(0, shares.Standby, 6);
        Assert.Equal(12.5, shares.Freed, 6);
        Assert.Equal(0, shares.Free, 6);
    }

    [Fact]
    public void MemoryShares_UnknownTotal_IsEmpty()
        => Assert.Equal(default(MemoryShares), BoostReadout.SplitMemory(0, 0, 0, null, 0));

    [Fact]
    public void PurgeTally_CountsEachPurgeOnce()
    {
        var first = DateTimeOffset.Parse("2026-10-24T20:51:00Z");
        var second = first.AddMinutes(4);

        var tally = PurgeTally.Empty
            .Add(null, first, 742)
            .Add(first, first, 742)     // the next readouts still name the same purge
            .Add(first, second, 300);

        Assert.Equal(1042, tally.TotalFreedMb);
        Assert.Equal(2, tally.Purges);
        Assert.Equal([first, second], tally.Recent);
    }

    [Fact]
    public void PurgeTally_AnOlderPurgeAfterPurgeNow_IsNotCountedAgain()
    {
        var helperPurge = DateTimeOffset.Parse("2026-10-24T20:51:00Z");
        var purgeNow = helperPurge.AddMinutes(4);

        var tally = PurgeTally.Empty
            .Add(null, helperPurge, 500)
            .Add(helperPurge, purgeNow, 200)
            // The helper's next readout still reports its own purge from before.
            .Add(purgeNow, helperPurge, 500);

        Assert.Equal(700, tally.TotalFreedMb);
        Assert.Equal(2, tally.Recent.Count);
    }

    [Fact]
    public void PurgeTally_KeepsOnlyTheRecentPurges()
    {
        var at = DateTimeOffset.Parse("2026-10-24T20:00:00Z");
        var tally = PurgeTally.Empty;
        for (var i = 0; i < PurgeTally.RecentLimit + 10; i++)
        {
            tally = tally.Add(at.AddSeconds(i), at.AddSeconds(i + 1), 1);
        }

        Assert.Equal(PurgeTally.RecentLimit + 10, tally.TotalFreedMb);
        Assert.Equal(PurgeTally.RecentLimit + 10, tally.Purges);
        Assert.Equal(PurgeTally.RecentLimit, tally.Recent.Count);
        Assert.Equal(at.AddSeconds(PurgeTally.RecentLimit + 10), tally.Recent[^1]);
    }
}
