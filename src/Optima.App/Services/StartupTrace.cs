using System.Diagnostics;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// Where a start spends its time: named moments in milliseconds since the process was created,
/// written to the log as one line once startup has finished. A mark costs one list append.
/// </summary>
internal static class StartupTrace
{
    private static readonly DateTime ProcessStart = ReadProcessStart();
    private static readonly List<(string Name, long Ms)> Marks = [];

    public static void Mark(string name)
    {
        var ms = (long)(DateTime.UtcNow - ProcessStart).TotalMilliseconds;
        lock (Marks)
        {
            Marks.Add((name, ms));
        }
    }

    public static void Report()
    {
        lock (Marks)
        {
            Log.Information("Startup timeline, ms since the process started: {Timeline}",
                string.Join(", ", Marks.Select(m => $"{m.Name} {m.Ms}")));
        }
    }

    private static DateTime ReadProcessStart()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime();
    }
}
