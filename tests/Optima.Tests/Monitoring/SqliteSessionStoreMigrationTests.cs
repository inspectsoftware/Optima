using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Monitoring;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class SqliteSessionStoreMigrationTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-store-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SqliteSessionStoreMigrationTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private SqliteSessionStore CreateStore() => new(_paths, NullLogger<SqliteSessionStore>.Instance);

    private void CreateV0Database()
    {
        // The exact pre-migration shape, with one legacy row.
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _paths.SessionsDatabase }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_name TEXT NOT NULL,
                game_package_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                average_fps REAL NOT NULL,
                one_percent_low_fps REAL NOT NULL,
                point_one_percent_low_fps REAL NOT NULL,
                average_frametime_ms REAL NOT NULL,
                p95_frametime_ms REAL NOT NULL,
                p99_frametime_ms REAL NOT NULL,
                fps_samples TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO sessions (profile_name, game_package_id, started_at, duration_seconds,
                sample_count, average_fps, one_percent_low_fps, point_one_percent_low_fps,
                average_frametime_ms, p95_frametime_ms, p99_frametime_ms, fps_samples)
            VALUES ('Legacy', 'com.example', '2026-01-01T00:00:00.0000000+00:00', 600,
                1000, 200, 150, 120, 5, 8, 10, '200.0,201.0');
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MigratesV0DatabaseAndKeepsOldRows()
    {
        CreateV0Database();

        var store = CreateStore();
        await store.InitializeAsync();

        var sessions = await store.GetSessionsAsync();
        var legacy = Assert.Single(sessions);
        Assert.Equal("Legacy", legacy.ProfileName);
        Assert.Equal(200, legacy.Stats.AverageFps);
        Assert.Equal(2, legacy.FpsSamples.Count);
        Assert.Empty(legacy.TweakIds);
        Assert.Equal(string.Empty, legacy.ProfileHash);
        Assert.Equal(LaunchKind.Play, legacy.LaunchKind);
        Assert.Null(legacy.Network);

        Assert.True(File.Exists(_paths.SessionsDatabase + ".bak"));
    }

    [Fact]
    public async Task FreshDatabaseReachesCurrentSchema()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _paths.SessionsDatabase }.ToString());
        await connection.OpenAsync();
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
        }
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='matches'";
        Assert.Equal("matches", (string)(await tables.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task V1DatabaseGainsV2ColumnsAndKeepsRows()
    {
        // Simulate a database left by v0.2.x: base table + v1 columns, user_version 1.
        CreateV0Database();
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _paths.SessionsDatabase }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                ALTER TABLE sessions ADD COLUMN tweak_ids TEXT NOT NULL DEFAULT '';
                ALTER TABLE sessions ADD COLUMN profile_hash TEXT NOT NULL DEFAULT '';
                ALTER TABLE sessions ADD COLUMN launch_kind TEXT NOT NULL DEFAULT 'play';
                ALTER TABLE sessions ADD COLUMN avg_ping_ms REAL NULL;
                ALTER TABLE sessions ADD COLUMN jitter_ms REAL NULL;
                ALTER TABLE sessions ADD COLUMN packet_loss_pct REAL NULL;
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            SqliteConnection.ClearAllPools();
        }

        var store = CreateStore();
        await store.InitializeAsync();

        var legacy = Assert.Single(await store.GetSessionsAsync());
        Assert.Equal("Legacy", legacy.ProfileName);
        Assert.Null(legacy.StatsDelta);
        Assert.Null(legacy.GameVersion);
        Assert.Empty(await store.GetMatchesAsync());
    }

    [Fact]
    public async Task StatsDeltaAndMatchesRoundTrip()
    {
        var store = CreateStore();
        var started = DateTimeOffset.Now;
        var id = await store.SaveSessionAsync(MakeRecord("Ranked night") with
        {
            StartedAt = started,
            StatsDelta = new Optima.Core.Stats.CopsProfileDelta(
                12,
                new Optima.Core.Stats.CopsModeStats(18, 11, 3, 1, 0),
                Optima.Core.Stats.CopsModeStats.Zero,
                Optima.Core.Stats.CopsModeStats.Zero),
            GameVersion = "1.52.0",
        });

        var loaded = Assert.Single(await store.GetSessionsByIdsAsync([id]));
        Assert.NotNull(loaded.StatsDelta);
        Assert.Equal(12, loaded.StatsDelta!.Season);
        Assert.Equal(18, loaded.StatsDelta.Ranked.Kills);
        Assert.Equal("1.52.0", loaded.GameVersion);

        var matchId = await store.SaveMatchAsync(new MatchRecord
        {
            SessionId = id,
            StartedAt = started,
            Mode = "ranked",
            Result = "win",
            Kills = 18,
            Deaths = 11,
            Assists = 3,
            Source = "auto",
        });
        var match = Assert.Single(await store.GetMatchesAsync());
        Assert.Equal(matchId, match.Id);
        Assert.Equal("win", match.Result);
        Assert.Equal(18, match.Kills);

        await store.UpdateMatchAsync(match with { Result = "loss", Source = "edited" });
        match = Assert.Single(await store.GetMatchesAsync());
        Assert.Equal("loss", match.Result);
        Assert.Equal("edited", match.Source);

        await store.DeleteMatchAsync(match.Id);
        Assert.Empty(await store.GetMatchesAsync());
    }

    [Fact]
    public async Task AttachStatsDeltaTargetsTheNewestSessionInWindow()
    {
        var store = CreateStore();
        var early = DateTimeOffset.Now.AddHours(-3);
        await store.SaveSessionAsync(MakeRecord("Old") with { StartedAt = early });
        var recent = DateTimeOffset.Now.AddMinutes(-10);
        var target = await store.SaveSessionAsync(MakeRecord("Fresh") with { StartedAt = recent });

        var delta = new Optima.Core.Stats.CopsProfileDelta(
            12, new Optima.Core.Stats.CopsModeStats(5, 4, 1, 1, 0),
            Optima.Core.Stats.CopsModeStats.Zero, Optima.Core.Stats.CopsModeStats.Zero);

        var attached = await store.AttachStatsDeltaAsync(delta, recent.AddMinutes(-2));
        Assert.Equal(target, attached);

        var loaded = (await store.GetSessionsByIdsAsync([target])).Single();
        Assert.Equal(5, loaded.StatsDelta!.Ranked.Kills);

        // No session in the window: nothing is touched.
        Assert.Null(await store.AttachStatsDeltaAsync(delta, DateTimeOffset.Now.AddMinutes(5)));
    }

    [Fact]
    public async Task NewFieldsRoundTrip()
    {
        var store = CreateStore();
        var record = new SessionRecord
        {
            ProfileName = "Competitive",
            GamePackageId = "com.example",
            StartedAt = DateTimeOffset.Now,
            Duration = TimeSpan.FromMinutes(20),
            Stats = new SessionStats { AverageFps = 240, SampleCount = 500 },
            FpsSamples = [240, 241],
            TweakIds = ["game-dvr-off", "hags-on"],
            ProfileHash = "abc123def456",
            LaunchKind = LaunchKind.Watch,
            Network = new NetworkQualityStats { AveragePingMs = 24.5, JitterMs = 1.2, PacketLossPct = 0.5, SampleCount = 100 },
        };

        var id = await store.SaveSessionAsync(record);
        var loaded = Assert.Single(await store.GetSessionsByIdsAsync([id]));

        Assert.Equal(["game-dvr-off", "hags-on"], loaded.TweakIds);
        Assert.Equal("abc123def456", loaded.ProfileHash);
        Assert.Equal(LaunchKind.Watch, loaded.LaunchKind);
        Assert.NotNull(loaded.Network);
        Assert.Equal(24.5, loaded.Network.AveragePingMs, 3);
        Assert.Equal(1.2, loaded.Network.JitterMs, 3);
        Assert.Equal(0.5, loaded.Network.PacketLossPct, 3);
    }

    [Fact]
    public async Task GetSessionsByIdsReturnsOnlyRequestedRows()
    {
        var store = CreateStore();
        var first = await store.SaveSessionAsync(MakeRecord("A"));
        await store.SaveSessionAsync(MakeRecord("B"));
        var third = await store.SaveSessionAsync(MakeRecord("C"));

        var loaded = await store.GetSessionsByIdsAsync([first, third]);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(["A", "C"], loaded.Select(s => s.ProfileName).ToArray());

        Assert.Empty(await store.GetSessionsByIdsAsync([]));
    }

    private static SessionRecord MakeRecord(string profile) => new()
    {
        ProfileName = profile,
        GamePackageId = "com.example",
        StartedAt = DateTimeOffset.Now,
        Duration = TimeSpan.FromMinutes(5),
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
