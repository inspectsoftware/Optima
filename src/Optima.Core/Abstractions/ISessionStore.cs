using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>
/// Where one session's run ends in the stored snapshot chain: a run is over when the next one starts,
/// so the next session's start snapshot is the only honest upper bound for this session's stats. The
/// newest session has no such bound (<see cref="HasNextSession"/> false) and has to be measured against
/// a live API reading instead.
/// </summary>
/// <param name="HasNextSession">Whether a newer session exists at all.</param>
/// <param name="Baseline">That session's start snapshot, or null when it has none on file.</param>
public sealed record SessionEndBoundary(bool HasNextSession, Stats.CopsSeasonStats? Baseline);

/// <summary>Session history persistence (§13/§14/§21).</summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<long> SaveSessionAsync(SessionRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// The same history without each session's per-second fps series, for lists that show numbers
    /// rather than graphs: loading a few hundred sessions otherwise means parsing a few hundred
    /// series nobody looks at. Read one session's samples with <see cref="GetSessionsByIdsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SessionRecord>> GetSessionSummariesAsync(int limit = 50, CancellationToken ct = default)
        => GetSessionsAsync(limit, ct);

    Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRecord>> GetSessionsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Writes a run's stats onto the newest session that started at or after <paramref name="windowStart"/>:
    /// the delta between the run's start and end snapshots, and the start snapshot itself. The baseline
    /// is stored even when there is no delta yet, because the refresh button recomputes from it once the
    /// API catches up. An empty delta leaves any delta already on the row alone.
    /// </summary>
    Task<long?> AttachStatsAsync(Stats.CopsProfileDelta? delta, Stats.CopsSeasonStats? baseline,
        DateTimeOffset windowStart, CancellationToken ct = default);

    /// <summary>
    /// Replaces one session's stats delta outright. This is the refresh path: it was measured by hand
    /// against a fresh API reading, so it wins over whatever the automatic pass wrote.
    /// </summary>
    Task<bool> UpdateStatsDeltaAsync(long sessionId, Stats.CopsProfileDelta delta, CancellationToken ct = default);

    /// <summary>The start snapshot of the next session after this one, which is where this session ends.</summary>
    Task<SessionEndBoundary> GetSessionEndBoundaryAsync(long sessionId, CancellationToken ct = default);

    Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task UpdateMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task DeleteMatchAsync(long matchId, CancellationToken ct = default);

    Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default);
}
