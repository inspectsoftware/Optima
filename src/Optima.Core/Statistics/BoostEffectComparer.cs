using Optima.Core.Boost;
using Optima.Core.Models;

namespace Optima.Core.Statistics;

/// <summary>
/// One Boost switch over the saved sessions: the median 1% low and average fps of the sessions it
/// was working in and of the sessions it was off in. Medians, not means: one session on a bad
/// server or with a download running would pull a mean a long way.
/// </summary>
public sealed record BoostEffect(
    string Key,
    string Name,
    int SessionsOn,
    int SessionsOff,
    double? OnePercentLowOn,
    double? OnePercentLowOff,
    double? AverageFpsOn,
    double? AverageFpsOff)
{
    /// <summary>At least <see cref="BoostEffectComparer.MinimumSessions"/> sessions on each side.</summary>
    public bool HasEnough => SessionsOn >= BoostEffectComparer.MinimumSessions && SessionsOff >= BoostEffectComparer.MinimumSessions;

    public double? OnePercentLowDelta => HasEnough ? OnePercentLowOn - OnePercentLowOff : null;

    public double? AverageFpsDelta => HasEnough ? AverageFpsOn - AverageFpsOff : null;
}

/// <summary>
/// Compares sessions by what Boost had on, for the Sessions page's "what helps" panel. It is a hint,
/// not a measurement: sessions differ in map, server, what else ran and the game's own updates, and
/// nothing here holds those still. What it can do is keep the comparison fair where it knows how:
/// one player's sessions only, long ones only, and only sessions where the switch was clearly working
/// or clearly off.
/// </summary>
public static class BoostEffectComparer
{
    /// <summary>Sessions needed on each side before a difference is shown.</summary>
    public const int MinimumSessions = 3;

    /// <summary>Shorter sessions are mostly menus and loading, whose frame rate says little about a match.</summary>
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMinutes(3);

    /// <summary>
    /// One row per Boost switch, in the BOOST page's order. A session counts as "on" for a switch
    /// that was working in it and as "off" for one that was off; one where it was on but never
    /// worked counts as neither. Sessions from before the record was kept, from another player,
    /// without fps data or no longer than <see cref="MinimumLength"/> are left out. The player is
    /// given by every key their sessions can carry (<see cref="BoostSessionRecorder.PlayerKeys"/>).
    /// </summary>
    public static IReadOnlyList<BoostEffect> Compare(IEnumerable<SessionRecord> sessions, params string[] players)
    {
        var usable = sessions
            .Where(s => s.Boost is { } boost && players.Contains(boost.Player, StringComparer.OrdinalIgnoreCase)
                && s.Duration > MinimumLength && s.Stats.HasData)
            .ToList();

        return [.. BoostFeatures.All.Select(feature =>
        {
            var on = usable.Where(s => s.Boost!.Applied.Contains(feature.Key)).Select(s => s.Stats).ToList();
            var off = usable.Where(s => !s.Boost!.On.Contains(feature.Key)).Select(s => s.Stats).ToList();
            return new BoostEffect(feature.Key, feature.Name, on.Count, off.Count,
                Median(on.Select(s => s.OnePercentLowFps)), Median(off.Select(s => s.OnePercentLowFps)),
                Median(on.Select(s => s.AverageFps)), Median(off.Select(s => s.AverageFps)));
        })];
    }

    /// <summary>The middle value, or the mean of the two middle ones; null for no values.</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
