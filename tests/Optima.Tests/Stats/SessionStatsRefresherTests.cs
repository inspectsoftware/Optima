using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Optima.Tests.Launch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Stats;

/// <summary>
/// The refresh button's contract: it measures against the session's stored start snapshot, it keeps
/// asking while the API has nothing, and it never invents numbers when there is nothing to measure.
/// </summary>
public sealed class SessionStatsRefresherTests : IDisposable
{
    private const int Season = 12;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionStore _store = new();
    private readonly SettingsService _settings;
    private readonly List<string> _queries = [];
    private CopsPlayerProfile? _profile;

    public SessionStatsRefresherTests()
    {
        var paths = new AppPaths(_tempRoot);
        paths.EnsureCreated();
        _settings = new SettingsService(paths, new JsonStore(NullLogger<JsonStore>.Instance),
            NullLogger<SettingsService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private SessionStatsRefresher CreateRefresher()
        => new(_store, _settings,
            (ign, accountId, _) =>
            {
                _queries.Add(ign);
                return Task.FromResult(_profile);
            },
            NullLogger<SessionStatsRefresher>.Instance);

    private async Task IdentifyPlayerAsync(string ign = "Player")
        => await _settings.SaveSettingsAsync(new AppSettings { PlayerIgn = ign });

    private long SeedSession(CopsSeasonStats? baseline, DateTimeOffset? started = null)
    {
        var id = _store.Saved.Count + 1;
        _store.Saved.Add(new SessionRecord
        {
            Id = id,
            ProfileName = "Competitive",
            GamePackageId = "com.criticalforceentertainment.criticalops",
            StartedAt = started ?? DateTimeOffset.Now.AddMinutes(-30),
            Duration = TimeSpan.FromMinutes(20),
            Stats = new SessionStats { AverageFps = 240, SampleCount = 500 },
            StatsBaseline = baseline,
        });
        return id;
    }

    private static CopsSeasonStats Baseline(long kills = 100, long deaths = 80, long wins = 10, long losses = 5)
        => new(Season, new CopsModeStats(kills, deaths, 20, wins, losses), CopsModeStats.Zero, CopsModeStats.Zero);

    private static CopsPlayerProfile ProfileAfter(CopsModeStats ranked)
        => new(4242, "Player", 40, [new CopsSeasonStats(Season, ranked, CopsModeStats.Zero, CopsModeStats.Zero)]);

    [Fact]
    public async Task MovesOnlyOnceTheApiPublishesTheMatch()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());

        // First reading is still the baseline (the API is behind), the second has the match.
        var readings = new Queue<CopsPlayerProfile?>([
            ProfileAfter(new CopsModeStats(100, 80, 20, 10, 5)),
            ProfileAfter(new CopsModeStats(118, 91, 23, 11, 5)),
        ]);
        var refresher = new SessionStatsRefresher(_store, _settings,
            (_, _, _) => Task.FromResult(readings.Dequeue()),
            NullLogger<SessionStatsRefresher>.Instance);

        var result = await refresher.RefreshAsync(id, attempts: 3, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.Updated, result.Status);
        Assert.True(result.Updated);
        Assert.Equal(18, result.Delta!.Ranked.Kills);
        var updated = Assert.Single(_store.UpdatedDeltas);
        Assert.Equal(id, updated.SessionId);
        Assert.Equal(18, updated.Delta.Ranked.Kills);
        Assert.Contains("ranked 18/11/3", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivesUpAfterTheConfiguredAttemptsAndSaysSo()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());
        _profile = ProfileAfter(new CopsModeStats(100, 80, 20, 10, 5));

        var result = await CreateRefresher().RefreshAsync(id, attempts: 2, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.NoMovement, result.Status);
        Assert.Empty(_store.UpdatedDeltas);
        Assert.Equal(2, _queries.Count);
        Assert.Contains("press refresh again", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnUnreachableApiSeparately()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());
        _profile = null;

        var result = await CreateRefresher().RefreshAsync(id, attempts: 2, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.ApiUnreachable, result.Status);
        Assert.Empty(_store.UpdatedDeltas);
    }

    [Fact]
    public async Task SessionsWithoutABaselineCannotBeRefreshed()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(baseline: null);
        _profile = ProfileAfter(new CopsModeStats(118, 91, 23, 11, 5));

        var result = await CreateRefresher().RefreshAsync(id, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.NoBaseline, result.Status);
        Assert.Empty(_queries);
        Assert.Empty(_store.UpdatedDeltas);
    }

    [Fact]
    public async Task WithoutAPlayerIdentityThereIsNobodyToAsk()
    {
        var id = SeedSession(Baseline());

        var result = await CreateRefresher().RefreshAsync(id, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.NoPlayerIdentity, result.Status);
        Assert.Empty(_queries);
    }

    [Fact]
    public async Task AMissingSessionIsReportedNotGuessed()
    {
        await IdentifyPlayerAsync();

        var result = await CreateRefresher().RefreshAsync(77, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.SessionMissing, result.Status);
        Assert.Empty(_store.UpdatedDeltas);
    }

    [Fact]
    public async Task ASingleMatchTheAutomaticPassMissedBecomesARow()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());
        _profile = ProfileAfter(new CopsModeStats(118, 91, 23, 11, 5));

        var result = await CreateRefresher().RefreshAsync(id, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.Updated, result.Status);
        var row = Assert.Single(_store.Matches);
        Assert.Equal(id, row.SessionId);
        Assert.Equal("ranked", row.Mode);
        Assert.Equal("win", row.Result);
        Assert.Equal(18, row.Kills);
        Assert.Equal("auto", row.Source);
    }

    [Fact]
    public async Task AnExistingAutomaticRowIsCorrectedInPlace()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());
        _profile = ProfileAfter(new CopsModeStats(118, 91, 23, 11, 5));

        // The automatic pass had already recorded a row for this session, with the wrong numbers.
        _store.Matches.Add(new MatchRecord
        {
            Id = 5,
            SessionId = id,
            StartedAt = DateTimeOffset.Now.AddMinutes(-30),
            Mode = "ranked",
            Result = "loss",
            Kills = 1,
            Deaths = 2,
            Assists = 3,
            Source = "auto",
        });

        var result = await CreateRefresher().RefreshAsync(id, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Equal(SessionStatsRefreshStatus.Updated, result.Status);
        var corrected = Assert.Single(_store.UpdatedMatches);
        Assert.Equal(5, corrected.Id);
        Assert.Equal("win", corrected.Result);
        Assert.Equal(18, corrected.Kills);
        Assert.Equal(11, corrected.Deaths);
        Assert.Equal(3, corrected.Assists);
        Assert.Single(_store.Matches);
    }

    [Fact]
    public async Task HandEnteredRowsAreNeverTouched()
    {
        await IdentifyPlayerAsync();
        var id = SeedSession(Baseline());
        _profile = ProfileAfter(new CopsModeStats(118, 91, 23, 11, 5));

        _store.Matches.Add(new MatchRecord
        {
            Id = 9,
            SessionId = id,
            StartedAt = DateTimeOffset.Now.AddMinutes(-30),
            Mode = "ranked",
            Result = "win",
            Kills = 18,
            Deaths = 11,
            Assists = 3,
            Source = "manual",
        });

        await CreateRefresher().RefreshAsync(id, attempts: 1, retryDelay: TimeSpan.Zero);

        Assert.Empty(_store.UpdatedMatches);
        Assert.Single(_store.Matches);
    }
}
