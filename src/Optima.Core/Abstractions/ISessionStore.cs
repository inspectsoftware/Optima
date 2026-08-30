using Optima.Core.Models;

namespace Optima.Core.Abstractions;

/// <summary>Session history persistence (§13/§14/§21).</summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<long> SaveSessionAsync(SessionRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default);

    Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default);

    /// <summary>Fetches specific sessions by row id (guided benchmark result sets).</summary>
    Task<IReadOnlyList<SessionRecord>> GetSessionsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Writes the stats delta onto the newest session that started at or after
    /// <paramref name="windowStart"/> (the one the orchestrator just saved).
    /// Returns its id, or null when no such session exists.
    /// </summary>
    Task<long?> AttachStatsDeltaAsync(Stats.CopsProfileDelta delta, DateTimeOffset windowStart, CancellationToken ct = default);

    Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task UpdateMatchAsync(MatchRecord match, CancellationToken ct = default);

    Task DeleteMatchAsync(long matchId, CancellationToken ct = default);

    Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default);
}
