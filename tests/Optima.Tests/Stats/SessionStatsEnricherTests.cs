using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Optima.Core.Stats;
using Optima.Tests.Launch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Stats;

public sealed class SessionStatsEnricherTests
{
    private static CopsProfileDelta Delta(CopsModeStats ranked, CopsModeStats? casual = null)
        => new(12, ranked, casual ?? CopsModeStats.Zero, CopsModeStats.Zero);

    private static readonly DateTimeOffset Started = new(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleRankedWinBecomesOneAutoMatch()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(18, 11, 3, 1, 0)), Started, sessionId: 7);

        var match = Assert.Single(matches);
        Assert.Equal("ranked", match.Mode);
        Assert.Equal("win", match.Result);
        Assert.Equal(18, match.Kills);
        Assert.Equal(11, match.Deaths);
        Assert.Equal(3, match.Assists);
        Assert.Equal(7, match.SessionId);
        Assert.Equal("auto", match.Source);
    }

    [Fact]
    public void SingleLossIsALoss()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(9, 14, 2, 0, 1)), Started, null);

        Assert.Equal("loss", Assert.Single(matches).Result);
    }

    [Fact]
    public void MultiMatchSessionsProduceNoAutoRows()
    {
        // Three ranked matches in one sitting: k/d cannot be attributed per match.
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(50, 40, 9, 2, 1)), Started, 3);

        Assert.Empty(matches);
    }

    [Fact]
    public void RankedAndCasualSinglesBothExtract()
    {
        var matches = SessionStatsEnricher.ExtractAutoMatches(
            Delta(new CopsModeStats(18, 11, 3, 1, 0), new CopsModeStats(12, 8, 4, 0, 1)),
            Started, 5);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Mode == "ranked" && m.Result == "win");
        Assert.Contains(matches, m => m.Mode == "casual" && m.Result == "loss");
    }

    [Fact]
    public void ZeroDeltaExtractsNothing()
        => Assert.Empty(SessionStatsEnricher.ExtractAutoMatches(
            Delta(CopsModeStats.Zero), Started, null));

    private static CopsPlayerProfile Profile(long userId, long kills, long wins)
        => new(userId, "p" + userId, 40, [new CopsSeasonStats(12, new CopsModeStats(kills, 80, 20, wins, 5), CopsModeStats.Zero, CopsModeStats.Zero)]);

    [Fact]
    public void TwoDifferentPlayersHaveNoDelta()
    {
        Assert.Null(CopsProfileDelta.Between(Profile(1, 100, 5), Profile(2, 4000, 210)));
        // An id that is not known (a snapshot stored before ids were kept) still measures.
        Assert.Equal(18, CopsProfileDelta.Between(Profile(0, 100, 5), Profile(2, 118, 6))!.Ranked.Kills);
    }

    [Fact]
    public async Task TrackingSomeoneElseDuringARunStillMeasuresThePlayerWhoStartedIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "optima-enrich-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        try
        {
            var settings = new SettingsService(paths, new JsonStore(NullLogger<JsonStore>.Instance), NullLogger<SettingsService>.Instance);
            await settings.SaveSettingsAsync(new AppSettings { PlayerIgn = "me", PlayerAccountId = 1 });
            var store = new FakeSessionStore();
            var fetched = new List<long?>();
            var myReadings = new Queue<CopsPlayerProfile>([Profile(1, 100, 5), Profile(1, 118, 6)]);
            Task<CopsPlayerProfile?> Fetch(string ign, long? id, CancellationToken ct)
            {
                lock (fetched)
                {
                    fetched.Add(id);
                    return Task.FromResult<CopsPlayerProfile?>(id == 1 ? myReadings.Dequeue() : Profile(2, 4000, 210));
                }
            }

            var presence = new GamePresenceService(new FakeProcessMonitor(), NullLogger<GamePresenceService>.Instance);
            using var enricher = new SessionStatsEnricher(presence, settings, store, Fetch, NullLogger<SessionStatsEnricher>.Instance);
            enricher.Start();

            presence.ApplyState(GameRuntimeState.Running, Started);
            await WaitFor(() => { lock (fetched) { return fetched.Count == 1; } });
            // Mid-run, Settings tracks a friend, which is what Track writes.
            await settings.UpdateSettingsAsync(s => s with { PlayerIgn = "friend", PlayerAccountId = 2 });
            presence.ApplyState(GameRuntimeState.NotRunning, Started.AddMinutes(20));
            await WaitFor(() => store.AttachedDeltas.Count == 1);

            Assert.Equal([1L, 1L], fetched);
            var attached = Assert.Single(store.AttachedDeltas);
            Assert.Equal(18, attached.Delta!.Ranked.Kills);
            Assert.Equal(1, attached.Baseline!.UserId);
            Assert.Equal(18, Assert.Single(store.Matches).Kills);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        // The end of a run waits a few seconds for the API to settle before it reads the profile again.
        for (var waited = 0; !condition(); waited += 50)
        {
            Assert.True(waited < 15000, "timed out");
            await Task.Delay(50);
        }
    }
}
