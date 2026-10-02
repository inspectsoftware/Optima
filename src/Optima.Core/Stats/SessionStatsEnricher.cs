using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Stats;

/// <summary>
/// Turns public-profile snapshots around a game run into per-session stat deltas and, when a delta contains exactly one
/// match of a mode, an attributable match row.
/// </summary>
public sealed class SessionStatsEnricher : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);

    private readonly GamePresenceService _presence;
    private readonly SettingsService _settings;
    private readonly ISessionStore _store;
    private readonly Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> _fetchProfile;
    private readonly ILogger<SessionStatsEnricher> _logger;

    private (CopsPlayerProfile Profile, DateTimeOffset At)? _startSnapshot;
    private bool _subscribed;

    public SessionStatsEnricher(
        GamePresenceService presence,
        SettingsService settings,
        ISessionStore store,
        Func<string, long?, CancellationToken, Task<CopsPlayerProfile?>> fetchProfile,
        ILogger<SessionStatsEnricher> logger)
    {
        _presence = presence;
        _settings = settings;
        _store = store;
        _fetchProfile = fetchProfile;
        _logger = logger;
    }

    public void Start()
    {
        if (!_subscribed)
        {
            _presence.PresenceChanged += OnPresenceChanged;
            _presence.GameExited += OnGameExited;
            _subscribed = true;
        }
    }

    private void OnPresenceChanged(PresenceChange change)
    {
        if (change.Current == GamePresence.InGame && change.Previous != GamePresence.InGame)
        {
            _ = Task.Run(() => SnapshotStartAsync(change.At));
        }
    }

    private async Task SnapshotStartAsync(DateTimeOffset at)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.PlayerIgn) && settings.PlayerAccountId is not > 0)
            {
                return;
            }
            var profile = await _fetchProfile(settings.PlayerIgn, settings.PlayerAccountId, CancellationToken.None).ConfigureAwait(false);
            if (profile is not null)
            {
                _startSnapshot = (profile, at);
                _logger.LogDebug("Start-of-run profile snapshot taken for {Name}", profile.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Start-of-run profile snapshot failed");
        }
    }

    private void OnGameExited(GameExit exit)
        => _ = Task.Run(() => EnrichAsync(exit));

    private async Task EnrichAsync(GameExit exit)
    {
        try
        {
            var start = _startSnapshot;
            _startSnapshot = null;
            if (start is null)
            {
                return;
            }

            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.PlayerIgn) && settings.PlayerAccountId is not > 0)
            {
                return;
            }

            await Task.Delay(SettleDelay).ConfigureAwait(false);
            var after = await _fetchProfile(settings.PlayerIgn, settings.PlayerAccountId, CancellationToken.None).ConfigureAwait(false);
            var delta = CopsProfileDelta.Between(start.Value.Profile, after);
            var moved = delta is { IsZero: false };

            var windowStart = start.Value.At - TimeSpan.FromMinutes(2);
            // The start-of-run snapshot is stored even when nothing moved yet: the settle delay is
            // short and the public API often publishes a finished match a little later, so the
            // Sessions page needs the baseline to re-ask and fill the gap in.
            var sessionId = await _store.AttachStatsAsync(
                moved ? delta : null, start.Value.Profile.CurrentSeason, windowStart).ConfigureAwait(false);
            if (!moved)
            {
                _logger.LogDebug("No stat movement during this run (session: {Session})",
                    sessionId?.ToString() ?? "none");
                return;
            }

            _logger.LogInformation(
                "Session stats delta recorded (session: {Session}): ranked {RK}/{RD}/{RA} {RW}W-{RL}L",
                sessionId?.ToString() ?? "none",
                delta!.Ranked.Kills, delta.Ranked.Deaths, delta.Ranked.Assists, delta.Ranked.Wins, delta.Ranked.Losses);

            foreach (var match in ExtractAutoMatches(delta, start.Value.At, sessionId))
            {
                await _store.SaveMatchAsync(match).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session stat enrichment failed");
        }
    }

    public static IReadOnlyList<MatchRecord> ExtractAutoMatches(
        CopsProfileDelta delta, DateTimeOffset startedAt, long? sessionId)
    {
        var matches = new List<MatchRecord>();
        Add("ranked", delta.Ranked);
        Add("casual", delta.Casual);
        Add("custom", delta.Custom);
        return matches;

        void Add(string mode, CopsModeStats stats)
        {
            if (stats.MatchesCounted != 1)
            {
                return;
            }
            matches.Add(new MatchRecord
            {
                SessionId = sessionId,
                StartedAt = startedAt,
                Mode = mode,
                Result = stats.Wins == 1 ? "win" : "loss",
                Kills = stats.Kills,
                Deaths = stats.Deaths,
                Assists = stats.Assists,
                Source = "auto",
            });
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _presence.PresenceChanged -= OnPresenceChanged;
            _presence.GameExited -= OnGameExited;
            _subscribed = false;
        }
    }
}
