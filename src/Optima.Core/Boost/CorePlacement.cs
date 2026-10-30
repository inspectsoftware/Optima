namespace Optima.Core.Boost;

/// <summary>
/// One logical processor as Windows' CPU set table describes it. <see cref="SchedulingClass"/> is
/// Windows' own ranking of the cores (on AMD it follows the preferred-core order the firmware
/// reports, higher is faster); <see cref="L3Bytes"/> is the size of the last-level cache it shares.
/// </summary>
public sealed record LogicalCpu(
    uint Id,
    ushort Group,
    byte LogicalIndex,
    byte Core,
    byte LastLevelCache,
    byte EfficiencyClass,
    byte SchedulingClass,
    bool Reserved,
    long L3Bytes);

/// <summary>
/// The CPU sets the game is given, the logical processor numbers they are, how many physical cores
/// that is out of all, and why; and the CPU sets left to everything else, where the programs Boost
/// holds back are sent.
/// </summary>
public sealed record CorePlacementPlan(IReadOnlyList<uint> CpuSetIds, IReadOnlyList<int> LogicalProcessors, int Cores, int TotalCores, string Reason)
{
    /// <summary>True when the plan leaves some core to everything else; a plan of every core changes nothing.</summary>
    public bool Narrows => Cores < TotalCores;

    public IReadOnlyList<uint> SpareCpuSetIds { get; init; } = [];
}

/// <summary>
/// Which cores the game's emulator (crosvm, whose virtual processors are ordinary threads of its
/// own) should run on. In order: the performance cores on a hybrid processor with enough of them; the cores under the
/// largest cache when the caches differ (the V-Cache die of a dual-die X3D), or else the die with
/// the fastest core; then, inside that, the fastest cores by Windows' ranking, leaving out the core
/// that holds processor 0 (where most interrupts land) and the slowest, but never going below
/// <see cref="MinCores"/>. Both threads of a core always go together.
/// </summary>
public static class CorePlacement
{
    /// <summary>The game is never placed on fewer physical cores than this.</summary>
    public const int MinCores = 6;

    /// <summary>At most this many cores are left to Windows and the rest.</summary>
    public const int SparedCores = 2;

    public static CorePlacementPlan? Choose(IReadOnlyList<LogicalCpu> cpus)
    {
        var usable = cpus.Where(c => !c.Reserved).ToList();
        var all = usable;
        // ponytail: one processor group only. Over 64 logical processors a process default CPU set
        // is ignored for threads in another group; per-thread sets per group would be the upgrade.
        if (usable.Count == 0 || usable.Select(c => c.Group).Distinct().Count() > 1)
        {
            return null;
        }
        var total = CoresIn(usable);
        var reasons = new List<string>();

        // A laptop with two or four performance cores keeps the efficiency cores as well: the
        // emulator's threads on four logical processors would cost more than slower cores do.
        var fastest = usable.Max(c => c.EfficiencyClass);
        var performance = usable.Where(c => c.EfficiencyClass == fastest).ToList();
        if (performance.Count < usable.Count && CoresIn(performance) >= MinCores)
        {
            usable = performance;
            reasons.Add("performance cores only");
        }

        var dies = usable.GroupBy(c => c.LastLevelCache).ToList();
        if (dies.Count > 1)
        {
            var largest = dies.Max(d => d.Max(c => c.L3Bytes));
            var die = dies.Any(d => d.Max(c => c.L3Bytes) != largest)
                ? dies.First(d => d.Max(c => c.L3Bytes) == largest)
                : dies.OrderByDescending(d => d.Max(c => c.SchedulingClass)).First();
            if (CoresIn(die) >= MinCores)
            {
                reasons.Add(dies.Any(d => d.Max(c => c.L3Bytes) != largest) ? "the die with the larger cache" : "one die, the one with the fastest core");
                usable = [.. die];
            }
        }

        // Physical cores, best first: Windows' ranking, then the lower core number. The core of
        // processor 0 is the first to go, then the slowest.
        var cores = usable.GroupBy(c => c.Core)
            .OrderByDescending(g => g.Max(c => c.SchedulingClass))
            .ThenBy(g => g.Key)
            .ToList();
        var spare = Math.Clamp(cores.Count - MinCores, 0, SparedCores);
        var leftOut = new List<IGrouping<byte, LogicalCpu>>();
        if (spare > 0 && cores.FirstOrDefault(g => g.Any(c => c.LogicalIndex == 0)) is { } first)
        {
            leftOut.Add(first);
            reasons.Add("core 0 left to Windows");
        }
        foreach (var core in Enumerable.Reverse(cores))
        {
            if (leftOut.Count >= spare)
            {
                break;
            }
            if (!leftOut.Contains(core))
            {
                leftOut.Add(core);
                reasons.Add("the slowest core left to the rest");
            }
        }
        var kept = cores.Where(g => !leftOut.Contains(g)).ToList();
        if (kept.Count > 0 && usable.Any(c => c.SchedulingClass != usable[0].SchedulingClass))
        {
            reasons.Add("fastest cores first by Windows' ranking");
        }

        var chosen = kept.SelectMany(g => g).OrderBy(c => c.Id).ToList();
        return new CorePlacementPlan([.. chosen.Select(c => c.Id)], [.. chosen.Select(c => (int)c.LogicalIndex)], kept.Count, total,
            reasons.Count == 0 ? "every core is alike" : string.Join(", ", reasons.Distinct()))
        {
            SpareCpuSetIds = [.. all.Select(c => c.Id).Except(chosen.Select(c => c.Id)).Order()],
        };
    }

    private static int CoresIn(IEnumerable<LogicalCpu> cpus) => cpus.Select(c => c.Core).Distinct().Count();
}
