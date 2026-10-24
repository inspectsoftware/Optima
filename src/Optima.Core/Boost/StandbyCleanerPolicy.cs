namespace Optima.Core.Boost;

/// <summary>
/// What the memory cleaner has released in this run of Optima, and when its recent purges were, for
/// the BOOST page. Built from readouts that each carry only the latest purge, so a purge counts once,
/// on the first readout that shows a purge time later than the one before it.
/// </summary>
public sealed record PurgeTally(long TotalFreedMb, int Purges, IReadOnlyList<DateTimeOffset> Recent)
{
    /// <summary>The page draws the last half hour of purges, which only happen when memory runs short; this is far more than that.</summary>
    public const int RecentLimit = 64;

    public static PurgeTally Empty { get; } = new(0, 0, []);

    /// <summary>
    /// Counts the purge a readout reports when it is newer than the last one seen. "Purge now" stamps
    /// its own time, so the helper's next readout, which still names its own older purge, must not
    /// count that one a second time; hence later, not merely different.
    /// </summary>
    public PurgeTally Add(DateTimeOffset? previousPurgeAt, DateTimeOffset? purgeAt, long freedMb)
        => purgeAt is { } at && (previousPurgeAt is not { } before || at > before)
            ? new PurgeTally(TotalFreedMb + Math.Max(0, freedMb), Purges + 1, [.. Recent.TakeLast(RecentLimit - 1), at])
            : this;
}

/// <summary>
/// The rule and the limits of the BOOST memory cleaner, in one place: the app validates what it
/// sends with them and the elevated helper validates what it receives with the same ones.
/// </summary>
public static class StandbyCleanerPolicy
{
    public const int DefaultFreeBelowMb = 1024;
    public const int DefaultStandbyAboveMb = 1024;
    public const int DefaultIntervalMs = 5000;

    public const int MinThresholdMb = 128;
    public const int MaxThresholdMb = 65536;
    public const int MinIntervalMs = 1000;
    public const int MaxIntervalMs = 60000;

    /// <summary>
    /// Purge only when memory is actually short AND the standby list is what holds it. Either test
    /// alone would purge a healthy cache: plenty of standby with plenty free costs nothing, and low
    /// free memory with a small standby list has nothing worth purging.
    /// </summary>
    public static bool ShouldPurge(long freeMb, long standbyMb, int freeBelowMb, int standbyAboveMb)
        => freeMb < freeBelowMb && standbyMb > standbyAboveMb;

    public static bool IsValid(int freeBelowMb, int standbyAboveMb, int intervalMs)
        => freeBelowMb is >= MinThresholdMb and <= MaxThresholdMb
            && standbyAboveMb is >= MinThresholdMb and <= MaxThresholdMb
            && intervalMs is >= MinIntervalMs and <= MaxIntervalMs;

    public static int ClampThreshold(int value) => Math.Clamp(value, MinThresholdMb, MaxThresholdMb);
}
