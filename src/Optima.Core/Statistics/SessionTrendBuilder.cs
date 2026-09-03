using Optima.Core.Models;

namespace Optima.Core.Statistics;

/// <summary>One session in chronological trend order, flagged when its configuration differs from the previous one.</summary>
public sealed record SessionTrendPoint(SessionRecord Session, bool ConfigChanged);

/// <summary>
/// Prepares session history for the TRENDS view: chronological order, sessions without captured frames dropped, and a
/// config-change flag wherever the profile content hash or the enabled tweak set differs from the session before it.
/// </summary>
public static class SessionTrendBuilder
{
    public static IReadOnlyList<SessionTrendPoint> Build(IReadOnlyList<SessionRecord> sessionsNewestFirst, int take = 20)
    {
        var chronological = sessionsNewestFirst
            .Where(s => s.Stats.HasData)
            .Take(take)
            .Reverse()
            .ToList();

        var points = new List<SessionTrendPoint>(chronological.Count);
        SessionRecord? previous = null;
        foreach (var session in chronological)
        {
            var changed = previous is not null && !SameConfiguration(previous, session);
            points.Add(new SessionTrendPoint(session, changed));
            previous = session;
        }
        return points;
    }

    private static bool SameConfiguration(SessionRecord a, SessionRecord b)
        => string.Equals(a.ProfileHash, b.ProfileHash, StringComparison.Ordinal)
            && a.TweakIds.SequenceEqual(b.TweakIds, StringComparer.Ordinal);
}
