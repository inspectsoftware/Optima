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

    /// <summary>
    /// A newer session exists but carries no start snapshot, so this run has no known end and
    /// cannot be measured. Only the newest session can be measured without one.
    /// </summary>
    NoEndSnapshot,
}

/// <summary>Outcome of a stats refresh: the verdict plus a line to show the user.</summary>
public sealed record SessionStatsRefreshResult(
    SessionStatsRefreshStatus Status,
    string Message,
    CopsProfileDelta? Delta)
{
    public bool Updated => Status == SessionStatsRefreshStatus.Updated;
}

/// <summary>Outcome of re-deriving the newest sessions' stats and their automatic match rows.</summary>
public sealed record SessionMatchesRefreshResult(
    SessionStatsRefreshStatus Status,
    string Message,
    int SessionsUpdated,
    int MatchesAdded,
    int MatchesCorrected)
{
    public bool Updated => SessionsUpdated > 0 || MatchesAdded > 0 || MatchesCorrected > 0;
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

    /// <summary>How many recent sessions the matches refresh re-derives in one pass.</summary>
    public const int DefaultMatchSessions = 16;

    private readonly ISessionStore _store;
    private readonly SettingsService _settings;
    private readonly Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> _fetchProfile;
    private readonly ILogger<SessionStatsRefresher> _logger;
    private readonly Func<bool> _runInProgress;

    /// <param name="runInProgress">
    /// True while a game is running. A session is only written to the history when its run ends, so
    /// during a run the newest row is the one before it, and the live profile already holds matches
    /// of the run in progress: measured against it, they were credited to the wrong session, and
    /// then listed a second time when the run ended.
    /// </param>
    public SessionStatsRefresher(
        ISessionStore store,
        SettingsService settings,
        Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> fetchProfile,
        ILogger<SessionStatsRefresher> logger,
        Func<bool>? runInProgress = null)
    {
        _store = store;
        _settings = settings;
        _fetchProfile = fetchProfile;
        _logger = logger;
        _runInProgress = runInProgress ?? (() => false);
    }

    private const string RunInProgressMessage =
        "A game is running, so where the newest session ended cannot be read from the live profile yet. " +
        "Refresh again once the game is closed.";

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

        var boundary = await _store.GetSessionEndBoundaryAsync(session.Id, ct).ConfigureAwait(false);
        if (boundary.HasNextSession && boundary.Baseline is null)
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoEndSnapshot,
                "The session that followed this one carries no start snapshot, so where this run ended is unknown, " +
                "and measuring it against the profile now would credit it with everything played since.", null);
        }

        // A finished run ends where the next one begins, and that answer is already on file, so a session
        // that is no longer the newest is settled without asking the API at all. This is also why measuring
        // an older session against the live profile would be wrong: it would include later runs.
        if (boundary.Baseline is { } end)
        {
            var settled = DeltaBetween(baseline, end);
            if (settled is null)
            {
                return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoMovement,
                    "The next session's snapshot shows no movement during this one, so there is nothing to record.",
                    null);
            }

            var written = await RecordAsync(session, settled, ct).ConfigureAwait(false);
            _logger.LogInformation("Session #{Id} re-measured from the next session's snapshot", session.Id);
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.Updated,
                (written.DeltaChanged ? "Re-measured" : "Already correct") + " from the next session's snapshot: " +
                Describe(settled) + "." + MatchNote(written.MatchesAdded, written.MatchesCorrected), settled);
        }

        if (_runInProgress())
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoEndSnapshot, RunInProgressMessage, null);
        }

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PlayerIgn) && settings.PlayerAccountId is not > 0)
        {
            return new SessionStatsRefreshResult(SessionStatsRefreshStatus.NoPlayerIdentity,
                "Set your in-game name in Settings first: the stats API is queried by player.", null);
        }

        // This is the newest session, so the live profile is its only end and the API may still be behind.
        var delay = retryDelay ?? DefaultRetryDelay;
        var tries = Math.Max(1, attempts);
        var reached = true;
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var profile = await _fetchProfile(settings.PlayerIgn, settings.PlayerAccountId, ct).ConfigureAwait(false);
            reached = profile is not null;
            var delta = DeltaBetween(baseline, profile?.CurrentSeason);
            if (delta is { IsZero: false })
            {
                var written = await RecordAsync(session, delta, ct).ConfigureAwait(false);
                _logger.LogInformation("Session #{Id} stats refreshed from the API (attempt {Attempt})",
                    session.Id, attempt);
                return new SessionStatsRefreshResult(SessionStatsRefreshStatus.Updated,
                    "The API caught up: " + Describe(delta) + "." +
                    MatchNote(written.MatchesAdded, written.MatchesCorrected), delta);
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

    /// <summary>
    /// Re-derives the recent sessions' stats and their automatic match rows from one API reading.
    ///
    /// The matches list is built from those deltas, so a match that is missing or wrong is an empty or
    /// wrong delta on the session it belongs to. This does the same job as <see cref="RefreshAsync"/> but
    /// for every recent session at once, which is what makes it useful: the row the user is looking at
    /// usually belongs to a session that is no longer the newest, and those are settled by the stored
    /// snapshot chain rather than by the API.
    /// </summary>
    public async Task<SessionMatchesRefreshResult> RefreshRecentMatchesAsync(
        int sessions = DefaultMatchSessions,
        int attempts = DefaultAttempts,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        var summaries = await _store.GetSessionSummariesAsync(Math.Max(1, sessions), ct).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            return new SessionMatchesRefreshResult(SessionStatsRefreshStatus.SessionMissing,
                "No sessions recorded yet, so there are no matches to re-derive.", 0, 0, 0);
        }

        var settings = await _settings.GetSettingsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.PlayerIgn) && settings.PlayerAccountId is not > 0)
        {
            return new SessionMatchesRefreshResult(SessionStatsRefreshStatus.NoPlayerIdentity,
                "Set your in-game name in Settings first: the stats API is queried by player.", 0, 0, 0);
        }

        // Only the newest session depends on the API, and only it can still be behind; retrying is
        // pointless the moment its run already reads as finished.
        var delay = retryDelay ?? DefaultRetryDelay;
        // With a game running the newest session is not measured at all (see the constructor), so
        // there is nothing to wait for; the older ones are settled by their stored snapshots.
        var running = _runInProgress();
        var tries = running ? 1 : Math.Max(1, attempts);
        CopsPlayerProfile? profile = null;
        for (var attempt = 1; attempt <= tries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            profile = await _fetchProfile(settings.PlayerIgn, settings.PlayerAccountId, ct).ConfigureAwait(false);
            var newest = DeltaFor(summaries, 0, profile?.CurrentSeason);
            var pending = profile is null || newest is null || newest.IsZero;
            if (!pending || attempt == tries)
            {
                break;
            }
            _logger.LogDebug("The newest session still reads as empty; asking the stats API again ({Attempt}/{Tries})",
                attempt, tries);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        if (profile is null)
        {
            return new SessionMatchesRefreshResult(SessionStatsRefreshStatus.ApiUnreachable,
                "The stats API could not be reached just now. Check the connection and try again.", 0, 0, 0);
        }

        var live = running ? null : profile.CurrentSeason;
        var updated = 0;
        var added = 0;
        var corrected = 0;
        var unmeasurable = 0;
        for (var index = 0; index < summaries.Count; index++)
        {
            var session = summaries[index];
            var delta = DeltaFor(summaries, index, live);
            if (delta is null)
            {
                // Either this session has no start snapshot, or the run after it has none, which
                // leaves this one's end unknown. Measuring it against the profile now would credit it
                // with everything played in between, so it is left alone and counted.
                unmeasurable++;
                continue;
            }
            if (delta.IsZero)
            {
                // Nothing moved during that run, so there is nothing to record and no match to derive.
                continue;
            }

            var written = await RecordAsync(session, delta, ct).ConfigureAwait(false);
            if (written.DeltaChanged)
            {
                updated++;
            }
            added += written.MatchesAdded;
            corrected += written.MatchesCorrected;
        }

        if (updated == 0 && added == 0 && corrected == 0)
        {
            return new SessionMatchesRefreshResult(SessionStatsRefreshStatus.NoMovement,
                $"Checked the last {summaries.Count} session{(summaries.Count == 1 ? string.Empty : "s")}: the API " +
                "reports nothing the history does not already have." +
                UnmeasurableNote(unmeasurable), 0, 0, 0);
        }

        var newestStillEmpty = !running && DeltaFor(summaries, 0, live) is null or { IsZero: true };
        var message = $"Re-derived {updated} session{(updated == 1 ? string.Empty : "s")} from the API" +
            (added + corrected > 0
                ? $": {added} match row{(added == 1 ? string.Empty : "s")} added, {corrected} corrected"
                : string.Empty) +
            "." + UnmeasurableNote(unmeasurable);
        if (newestStillEmpty)
        {
            message += " The newest session still shows no movement; the API may not have published it yet - " +
                "press again in a minute.";
        }
        return new SessionMatchesRefreshResult(SessionStatsRefreshStatus.Updated, message, updated, added, corrected);
    }

    /// <summary>One session's delta: bounded by the next session's snapshot, or by the live reading for the newest.</summary>
    private static CopsProfileDelta? DeltaFor(IReadOnlyList<SessionRecord> newestFirst, int index, CopsSeasonStats? live)
    {
        if (newestFirst[index].StatsBaseline is not { } start)
        {
            return null;
        }
        var end = index == 0 ? live : newestFirst[index - 1].StatsBaseline;
        return DeltaBetween(start, end);
    }

    /// <summary>The delta between two snapshots, or null when the end of the run is unknown.</summary>
    private static CopsProfileDelta? DeltaBetween(CopsSeasonStats start, CopsSeasonStats? end)
        => end is null ? null : CopsProfileDelta.Between(AsBaseline(start), AsBaseline(end));

    /// <summary>Writes a measured delta onto its session when it says something new, and lines up its match rows either way.</summary>
    private async Task<(bool DeltaChanged, int MatchesAdded, int MatchesCorrected)> RecordAsync(
        SessionRecord session, CopsProfileDelta delta, CancellationToken ct)
    {
        var stored = session.StatsDelta is { IsZero: false } ? session.StatsDelta : null;
        var changed = stored != delta;
        if (changed)
        {
            await _store.UpdateStatsDeltaAsync(session.Id, delta, ct).ConfigureAwait(false);
        }

        // Reconciled even when the delta was already right: a delta the automatic pass recorded can
        // still be missing its match row, which is exactly the case this button exists for.
        var rows = await ReconcileAutoMatchesAsync(session, delta, ct).ConfigureAwait(false);
        return (changed, rows.Added, rows.Corrected);
    }

    private static string MatchNote(int added, int corrected)
        => added + corrected == 0 ? string.Empty : $" ({added} match row{(added == 1 ? string.Empty : "s")} added, {corrected} corrected)";

    private static string UnmeasurableNote(int unmeasurable)
        => unmeasurable == 0
            ? string.Empty
            : $" {unmeasurable} session{(unmeasurable == 1 ? string.Empty : "s")} had no snapshot to measure against.";

    /// <summary>The stored snapshot dressed as a profile, which is the shape the delta maths takes.</summary>
    private static CopsPlayerProfile AsBaseline(CopsSeasonStats baseline)
        => new(0, string.Empty, 0, [baseline]);

    /// <summary>
    /// Brings the automatic match rows in line with the refreshed delta: a mode that still holds exactly
    /// one decided match has its row corrected, and one that only now qualifies gets the row the automatic
    /// pass could not create. Hand-entered and edited rows are never touched.
    /// </summary>
    private async Task<(int Added, int Corrected)> ReconcileAutoMatchesAsync(
        SessionRecord session, CopsProfileDelta delta, CancellationToken ct)
    {
        var added = 0;
        var corrected = 0;
        try
        {
            var extracted = SessionStatsEnricher.ExtractAutoMatches(delta, session.StartedAt, session.Id);
            if (extracted.Count == 0)
            {
                return (0, 0);
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
                        added++;
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
                    corrected++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconciling the automatic matches for session #{Id} failed", session.Id);
        }
        return (added, corrected);
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
