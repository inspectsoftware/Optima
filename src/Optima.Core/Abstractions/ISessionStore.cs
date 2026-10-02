using Optima.Core.Models;

namespace Optima.Core.Abstractions;

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

    Task<long?> AttachStatsDeltaAsync(Stats.CopsProfileDelta delta, DateTimeOffset windowStart, CancellationToken ct = default);

    Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task UpdateMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task DeleteMatchAsync(long matchId, CancellationToken ct = default);

    Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default);
}
