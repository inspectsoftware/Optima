namespace Optima.Core.Boost;

/// <summary>The processor as the BOOST page's resource bar splits it, in percent of the whole machine; the four add up to 100.</summary>
public readonly record struct ProcessorShares(double Game, double HeldBack, double Other, double Idle);

/// <summary>Physical memory as the resource bar splits it, in percent of the total; the five add up to 100.</summary>
public readonly record struct MemoryShares(double Game, double Other, double Standby, double Freed, double Free);

/// <summary>
/// The arithmetic behind the BOOST page's live readout. The inputs come from different places at
/// slightly different moments (the hardware monitor, the helper's memory readout, the page's own
/// sampling of demoted programs), so every split clamps rather than trusting them to agree: a bar
/// whose parts add up to 103% is worse than one that is a percent off.
/// </summary>
public static class BoostReadout
{
    /// <summary>Windows' default timer tick, 64 to the second.</summary>
    public const double WindowsTickMs = 15.625;

    /// <summary>
    /// A process's share of the whole processor over an interval, from how much processor time it
    /// used in it. One busy thread on an eight-thread machine is 12.5%, as Task Manager counts it.
    /// </summary>
    public static double CpuPercent(TimeSpan cpuTime, TimeSpan elapsed, int logicalProcessors)
        => elapsed <= TimeSpan.Zero || logicalProcessors <= 0
            ? 0
            : Math.Clamp(100.0 * cpuTime.Ticks / (elapsed.Ticks * (double)logicalProcessors), 0, 100);

    /// <summary>How many times finer than Windows' default a timer resolution is: 1.0 ms is 15.6 times.</summary>
    public static double FinerBy(double resolutionMs) => resolutionMs > 0 ? WindowsTickMs / resolutionMs : 1;

    /// <summary>
    /// The timer tick the page shows as Optima's. Windows' clock is the finest resolution any program
    /// asks for, often another program's, and where a request stays inside the program that made it
    /// that is not the tick of a program that did not ask. So it is what Optima holds when it holds
    /// one, Windows' clock where requests reach every program, and otherwise Windows' default.
    /// </summary>
    public static double TimerTickMs(bool holding, double requestedMs, double clockMs, bool reachesEveryProgram)
        => holding ? requestedMs : reachesEveryProgram && clockMs > 0 ? clockMs : WindowsTickMs;

    /// <summary>
    /// The processor's busy time split into the game, the programs Boost holds back and everything
    /// else, with the rest idle. The game's share is taken first and the held-back share from what
    /// is left, so neither can push the bar past the processor's total.
    /// </summary>
    public static ProcessorShares SplitProcessor(double busyPercent, double gamePercent, double heldBackPercent)
    {
        var busy = Math.Clamp(busyPercent, 0, 100);
        var game = Math.Clamp(gamePercent, 0, busy);
        var heldBack = Math.Clamp(heldBackPercent, 0, busy - game);
        return new ProcessorShares(game, heldBack, busy - game - heldBack, 100 - busy);
    }

    /// <summary>
    /// Physical memory split into the game, everything else in use, the standby cache, what the last
    /// purge freed and the rest of the free memory. The standby list is only known from the helper's
    /// readout; without it, it is passed as null and everything not in use counts as free.
    /// <paramref name="freedMb"/> is carved out of free memory and never more than there is of it.
    /// </summary>
    public static MemoryShares SplitMemory(ulong totalBytes, ulong usedBytes, ulong gameBytes, long? standbyMb, long freedMb)
    {
        if (totalBytes == 0)
        {
            return default;
        }
        const double Mb = 1024.0 * 1024;
        var total = totalBytes / Mb;
        var used = Math.Min(usedBytes / Mb, total);
        var game = Math.Min(gameBytes / Mb, used);
        var available = total - used;
        var standby = Math.Clamp(standbyMb ?? 0, 0, available);
        var free = available - standby;
        var freed = Math.Clamp(freedMb, 0, free);
        free -= freed;
        return new MemoryShares(
            100 * game / total,
            100 * (used - game) / total,
            100 * standby / total,
            100 * freed / total,
            100 * free / total);
    }
}
