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
}
