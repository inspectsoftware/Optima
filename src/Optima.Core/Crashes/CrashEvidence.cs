namespace Optima.Core.Crashes;

/// <summary>What the logcat tail says about how the game ended.</summary>
public sealed record CrashEvidence(
    bool FatalSeen,
    bool ForceStopSeen,
    IReadOnlyList<string> ExcerptLines)
{
    public static readonly CrashEvidence Empty = new(false, false, []);
}

/// <summary>
/// Pure extraction of crash-relevant lines from a logcat tail. The interesting lines are
/// the game's own (package id), Android's process lifecycle (ActivityManager), and the
/// classic failure markers. Kept free of IO so it is trivially testable.
/// </summary>
public static class CrashSignals
{
    private static readonly string[] FatalMarkers =
    [
        "FATAL EXCEPTION",
        "Fatal signal",
        "ANR in",
        "tombstone",
        "beginning of crash",
    ];

    public static CrashEvidence Extract(IReadOnlyList<string> lines, string gamePackageId, int maxExcerptLines = 120)
    {
        if (lines.Count == 0)
        {
            return CrashEvidence.Empty;
        }

        var fatal = false;
        var forceStop = false;
        var excerpt = new List<string>();

        foreach (var line in lines)
        {
            var isGameLine = line.Contains(gamePackageId, StringComparison.OrdinalIgnoreCase);
            var isLifecycle = line.Contains("ActivityManager", StringComparison.Ordinal);
            var isFatal = FatalMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));

            if (isFatal)
            {
                fatal = true;
            }
            if (isGameLine && line.Contains("Force stopping", StringComparison.OrdinalIgnoreCase))
            {
                forceStop = true;
            }
            if (isGameLine || isFatal || (isLifecycle && isGameLine))
            {
                excerpt.Add(line);
            }
        }

        if (excerpt.Count > maxExcerptLines)
        {
            excerpt = excerpt[^maxExcerptLines..];
        }
        return new CrashEvidence(fatal, forceStop, excerpt);
    }

    /// <summary>
    /// The capture decision: a bundle is written only when the logcat carries a real
    /// failure marker. A quiet exit, even one where the emulator stayed up (which is how
    /// a normal menu-quit also looks from Windows), is not a crash.
    /// </summary>
    public static bool ShouldCapture(CrashEvidence evidence) => evidence.FatalSeen;
}
