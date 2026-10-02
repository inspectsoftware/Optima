using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Stats;

/// <summary>How one on-demand stats refresh ended, so the page can say something honest about it.</summary>
public enum SessionStatsRefreshStatus
{
    /// <summary>The API had caught up and the session's delta was rewritten.</summary>
    Updated,

    /// <summary>The session is not in the store any more.</summary>
    SessionMissing,

    /// <summary>The session carries no start-of-run snapshot, so there is nothing to measure against.</summary>
    NoBaseline,

    /// <summary>No in-game name or account id is configured, so there is nobody to ask about.</summary>
    NoPlayerIdentity,

    /// <summary>The API could not be reached at all.</summary>
    ApiUnreachable,

    /// <summary>The API answered, but it still shows no movement since the session started.</summary>
    NoMovement,
}

/// <summary>Outcome of a stats refresh: the verdict plus a line to show the user.</summary>
public sealed record SessionStatsRefreshResult(
    SessionStatsRefreshStatus Status,
    string Message,
    CopsProfileDelta? Delta)
{
    public bool Updated => Status == SessionStatsRefreshStatus.Updated;
}

/// <summary>
/// Re-reads the player's public profile for one finished session and recomputes that session's stat
/// delta against the snapshot taken when it started.
///
/// This exists because the automatic enrichment reads the API a few seconds after the game exits and
/// the public stats API does not always have the match by then: a session can end up with an empty or
/// short delta, or no match row at all. Pressing refresh asks again - up to <see cref="DefaultAttempts"/>
/// times, spaced out - until the numbers move, so the case that needs it is the one where it works.
/// </summary>
public sealed class SessionStatsRefresher
{
    /// <summary>How many times one press asks the API before reporting that nothing moved.</summary>
    public const int DefaultAttempts = 3;

    /// <summary>Gap between those asks: a match usually shows up within a handful of seconds.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ISessionStore _store;
    private readonly SettingsService _settings;
    private readonly Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> _fetchProfile;
    private readonly ILogger<SessionStatsRefresher> _logger;

    public SessionStatsRefresher(
        ISessionStore store,
        SettingsService settings,
        Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> fetchProfile,
        ILogger<SessionStatsRefresher> logger)
    {
        _store = store;
        _settings = settings;
        _fetchProfile = fetchProfile;
        _logger = logger;
    }

    /// <summary>
    /// Recomputes one session's stats from a fresh API reading. Passing <paramref name="attempts"/>
    /// of 1 asks once; a zero <paramref name="retryDelay"/> makes the retries immediate (tests).
    /// </summary>
    public async Task<SessionStatsRefreshResult> RefreshAsync(
        long sessionId,
        int attempts = DefaultAttempts,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        var sessions = await _store.GetSessionsByIdsAsync([sessionId], ct).ConfigureAwait(false);
        var session = sessions.FirstOrDefault();
        if (session is null)
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.SessionMissing,
                "That session is no longer in the history.", null);
        }

        if (session.StatsBaseline is not { } baseline)
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoBaseline,
                "This session has no start-of-run snapshot on file (it predates snapshot history), so there is " +
                "nothing to compare a fresh reading against. Sessions recorded from here on can be re-queried.",
                null);
        }

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PlayerIgn) && settings.PlayerAccountId is not > 0)
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoPlayerIdentity,
                "Set your in-game name in Settings first: the stats API is queried by player.", null);
        }

        var delay = retryDelay ?? DefaultRetryDelay;
        var tries = Math.Max(1, attempts);
        var reached = true;
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var profile = await _fetchProfile(settings.PlayerIgn, settings.PlayerAccountId, ct).ConfigureAwait(false);
            reached = profile is not null;
            if (profile is not null)
            {
                var delta = CopsProfileDelta.Between(AsBaseline(baseline), profile);
                if (delta is { IsZero: false })
                {
                    await _store.UpdateStatsDeltaAsync(session.Id, delta, ct).ConfigureAwait(false);
                    await ReconcileAutoMatchesAsync(session, delta, ct).ConfigureAwait(false);
                    _logger.LogInformation("Session #{Id} stats refreshed from the API (attempt {Attempt})",
                        session.Id, attempt);
                    return new SessionStatsRefreshResult(SessionStatsRefreshStatus.Updated,
                        "The API caught up: " + Describe(delta) + ".", delta);
                }
            }

            if (attempt < tries)
            {
                _logger.LogDebug("Session #{Id} stats refresh attempt {Attempt} found nothing yet", session.Id, attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return reached
            ? new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoMovement,
                $"The API still shows no movement since this session started, after {tries} tries. It may not have " +
                "published the match yet - press refresh again in a minute.", null)
            : new SessionStatsRefreshResult(SessionStatsRefreshStatus.ApiUnreachable,
                "The stats API could not be reached just now. Check the connection and try again.", null);
    }

    /// <summary>The stored snapshot dressed as a profile, which is the shape the delta maths takes.</summary>
    private static CopsPlayerProfile AsBaseline(CopsSeasonStats baseline)
        => new(0, string.Empty, 0, [baseline]);

    /// <summary>
    /// Brings the automatic match rows in line with the refreshed delta: a mode that still holds exactly
    /// one decided match has its row corrected, and one that only now qualifies gets the row the automatic
    /// pass could not create. Hand-entered and edited rows are never touched.
    /// </summary>
    private async Task ReconcileAutoMatchesAsync(SessionRecord session, CopsProfileDelta delta, CancellationToken ct)
    {
        try
        {
            var extracted = SessionStatsEnricher.ExtractAutoMatches(delta, session.StartedAt, session.Id);
            if (extracted.Count == 0)
            {
                return;
            }

            var existing = await _store.GetMatchesAsync(200, ct).ConfigureAwait(false);
            foreach (var match in extracted)
            {
                var forMode = existing
                    .Where(m => m.SessionId == session.Id
                        && string.Equals(m.Mode, match.Mode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var current = forMode.FirstOrDefault(m =>
                    string.Equals(m.Source, "auto", StringComparison.OrdinalIgnoreCase));

                if (current is null)
                {
                    // A row the user typed for this mode is the record of that match; adding an
                    // automatic one on top of it would show the same round twice.
                    if (forMode.Count == 0)
                    {
                        await _store.SaveMatchAsync(match, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                var same = current.Kills == match.Kills && current.Deaths == match.Deaths
                    && current.Assists == match.Assists
                    && string.Equals(current.Result, match.Result, StringComparison.Ordinal);
                if (!same)
                {
                    await _store.UpdateMatchAsync(current with
                    {
                        Result = match.Result,
                        Kills = match.Kills,
                        Deaths = match.Deaths,
                        Assists = match.Assists,
                    }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconciling the automatic matches for session #{Id} failed", session.Id);
        }
    }

    /// <summary>Compact one-line summary of a delta: "ranked 18/11/3 · 1W-0L".</summary>
    private static string Describe(CopsProfileDelta delta)
    {
        var parts = new List<string>();
        Add("ranked", delta.Ranked);
        Add("casual", delta.Casual);
        Add("custom", delta.Custom);
        return string.Join(" · ", parts);

        void Add(string mode, CopsModeStats stats)
        {
            if (stats.IsZero)
            {
                return;
            }
            parts.Add($"{mode} {stats.Kills}/{stats.Deaths}/{stats.Assists} · {stats.Wins}W-{stats.Losses}L");
        }
    }
}
