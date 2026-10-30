using System.Globalization;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>
/// One driver's share of the kernel's deferred work over a latency scan, in microseconds: the
/// longest single DPC and ISR it ran, and all the time its routines took together.
/// </summary>
public sealed record DriverLatency(string Driver, double LongestDpcUs, double LongestIsrUs, double TotalUs, long Count)
{
    /// <summary>The longer of the two: either one holds every other thread on that core until it ends.</summary>
    public double LongestUs => Math.Max(LongestDpcUs, LongestIsrUs);
}

/// <summary>
/// Adds up the DPC and ISR events of a kernel trace and says which driver ran them. The trace
/// names a routine by its address only, so the loaded drivers are recorded beside them (from the
/// image events the same trace carries), and the addresses are matched to drivers once the trace
/// is over: the events do not promise that a driver's image event comes before its first DPC.
/// Fed from one thread, the one the trace is processed on, and read after it has stopped.
/// </summary>
public sealed class DriverLatencyTally
{
    /// <summary>The lowest kernel address on x64. A user program's image loads are in the trace too, and none of them runs a DPC.</summary>
    public const ulong KernelSpaceStart = 0xFFFF_8000_0000_0000;

    /// <summary>The name for routines outside every image the trace recorded. It is not a file, so it is never named as one.</summary>
    public const string UnknownDriver = "unknown";

    private readonly List<(ulong Base, ulong End, string Driver)> _modules = [];
    private readonly Dictionary<ulong, Routine> _routines = [];

    public void AddModule(ulong imageBase, long size, string fileName)
    {
        if (imageBase < KernelSpaceStart || size <= 0 || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        _modules.Add((imageBase, imageBase + (ulong)size, FileNameOf(fileName)));
    }

    public void AddDpc(ulong routine, double microseconds) => Add(routine, microseconds, isr: false);

    public void AddIsr(ulong routine, double microseconds) => Add(routine, microseconds, isr: true);

    /// <summary>The drivers that ran anything, the one with the longest single call first.</summary>
    public IReadOnlyList<DriverLatency> Rank(int top = 10)
    {
        var modules = _modules.OrderBy(m => m.Base).ToArray();
        var bases = modules.Select(m => m.Base).ToArray();
        var drivers = new Dictionary<string, DriverLatency>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, routine) in _routines)
        {
            // The last image that starts at or below the address, when the address is inside it.
            var at = Array.BinarySearch(bases, address);
            if (at < 0)
            {
                at = ~at - 1;
            }
            var name = at >= 0 && address < modules[at].End ? modules[at].Driver : UnknownDriver;
            drivers[name] = drivers.TryGetValue(name, out var so)
                ? so with
                {
                    LongestDpcUs = Math.Max(so.LongestDpcUs, routine.LongestDpc),
                    LongestIsrUs = Math.Max(so.LongestIsrUs, routine.LongestIsr),
                    TotalUs = so.TotalUs + routine.Total,
                    Count = so.Count + routine.Count,
                }
                : new DriverLatency(name, routine.LongestDpc, routine.LongestIsr, routine.Total, routine.Count);
        }
        return drivers.Values
            .OrderByDescending(d => d.LongestUs)
            .ThenByDescending(d => d.TotalUs)
            .Take(top)
            .ToList();
    }

    private void Add(ulong address, double microseconds, bool isr)
    {
        if (microseconds < 0 || double.IsNaN(microseconds))
        {
            return;
        }
        if (!_routines.TryGetValue(address, out var routine))
        {
            _routines[address] = routine = new Routine();
        }
        if (isr)
        {
            routine.LongestIsr = Math.Max(routine.LongestIsr, microseconds);
        }
        else
        {
            routine.LongestDpc = Math.Max(routine.LongestDpc, microseconds);
        }
        routine.Total += microseconds;
        routine.Count++;
    }

    /// <summary>The file only: the trace gives "\SystemRoot\System32\drivers\x.sys" or a drive path, and the folder says nothing.</summary>
    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private sealed class Routine
    {
        public double LongestDpc;
        public double LongestIsr;
        public double Total;
        public long Count;
    }
}

/// <summary>
/// What a driver latency scan means for the player. A DPC or ISR holds its core until it ends, and
/// whatever the game wanted to run there waits: a single one past a millisecond is a stutter or a
/// dropped sound, and the driver that ran it is almost always one that wants updating or rolling back.
/// </summary>
public static class DriverLatencyScan
{
    public const string CheckName = "Driver Latency";
    public const string HighCode = "DRIVER_LATENCY_HIGH";
    public const string ElevatedCode = "DRIVER_LATENCY_ELEVATED";

    /// <summary>A single call this long is a hitch you can feel: the issue.</summary>
    public const double HighUs = 1000;

    /// <summary>Long enough to note, not long enough to blame for a stutter.</summary>
    public const double ElevatedUs = 500;

    public static DiagnosticResult ToResult(IReadOnlyList<DriverLatency> drivers, int seconds)
    {
        if (drivers.Count == 0)
        {
            return new DiagnosticResult
            {
                CheckName = CheckName,
                Status = DiagnosticStatus.Skipped,
                Reason = $"Windows recorded no DPC or ISR in {seconds} seconds, so there is nothing to rank.",
            };
        }

        var details = string.Join("\n", drivers.Select(Describe));
        var worst = drivers.MaxBy(d => d.LongestUs)!;
        var busiest = drivers.MaxBy(d => d.TotalUs)!;
        var busiestLine = $"Most time in all: {busiest.Driver}, {Us(busiest.TotalUs)} over {seconds} seconds.";
        if (worst.LongestUs <= ElevatedUs)
        {
            return new DiagnosticResult
            {
                CheckName = CheckName,
                Status = DiagnosticStatus.Pass,
                Reason = $"No driver held a core for more than {Us(ElevatedUs)} at once. The longest was {worst.Driver}, {Us(worst.LongestUs)}. {busiestLine}",
                Details = details,
                IssueCode = HighCode,
            };
        }

        var high = worst.LongestUs > HighUs;
        var named = drivers
            .Where(d => d.LongestUs > (high ? HighUs : ElevatedUs) && d.Driver != DriverLatencyTally.UnknownDriver)
            .Select(d => d.Driver)
            .ToList();
        // Only the routines no recorded image owns got that far: there is no file to tell the
        // player about, so say so instead of asking them to update a driver called "unknown".
        var update = named.Count > 0
            ? $"Update {string.Join(", ", named)} from the maker of the device, or roll it back if the trouble started with an update "
            : "The scan could not tell which driver it was. Update your drivers from the maker of each device, or roll back one that changed lately ";
        return new DiagnosticResult
        {
            CheckName = CheckName,
            Status = DiagnosticStatus.Warning,
            Reason = $"{(worst.Driver == DriverLatencyTally.UnknownDriver ? "A driver the scan could not name" : worst.Driver)} ran a single {(worst.LongestIsrUs >= worst.LongestDpcUs ? "ISR" : "DPC")} of {Us(worst.LongestUs)}"
                + (high ? ", long enough to cause a stutter or a crackle." : ". Not enough for a stutter on its own, worth knowing.")
                + $" {busiestLine}",
            RecommendedFix = update
                + "(Device Manager > the device > Driver). Wi-Fi, audio, RGB and storage drivers are the usual causes.",
            Details = details,
            IssueCode = high ? HighCode : ElevatedCode,
        };
    }

    /// <summary>
    /// The result, and a pass for the code it does not carry: a scan that finds only a note now
    /// closes the issue an earlier scan opened, and the other way round. A skipped scan says nothing.
    /// </summary>
    public static IReadOnlyList<DiagnosticResult> Reports(DiagnosticResult result)
    {
        if (result.Status == DiagnosticStatus.Skipped)
        {
            return [result];
        }
        var reports = new List<DiagnosticResult> { result };
        foreach (var code in new[] { HighCode, ElevatedCode })
        {
            if (code != result.IssueCode)
            {
                reports.Add(result with { Status = DiagnosticStatus.Pass, IssueCode = code });
            }
        }
        return reports;
    }

    private static string Describe(DriverLatency d)
        => $"{d.Driver}: longest DPC {Us(d.LongestDpcUs)}, longest ISR {Us(d.LongestIsrUs)}, {Us(d.TotalUs)} in all over {d.Count.ToString(CultureInfo.InvariantCulture)} calls";

    private static string Us(double microseconds) => microseconds.ToString("0", CultureInfo.InvariantCulture) + " us";
}
