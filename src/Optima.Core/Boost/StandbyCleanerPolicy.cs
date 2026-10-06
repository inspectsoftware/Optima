namespace Optima.Core.Boost;

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
