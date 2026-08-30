using System.Globalization;
using System.Text.Json;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring;

/// <summary>Session history in SQLite (%LOCALAPPDATA%\Optima\sessions.db, §13/§21).</summary>
public sealed class SqliteSessionStore : ISessionStore
{
    /// <summary>Current schema version stored in PRAGMA user_version.</summary>
    private const int SchemaVersion = 2;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionStore> _logger;
    private bool _initialized;

    public SqliteSessionStore(AppPaths paths, ILogger<SqliteSessionStore> logger)
    {
        _databasePath = paths.SessionsDatabase;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.SessionsDatabase }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // The base table is deliberately created at its original (v0) shape and brought to the
        // current schema by the same migrations an existing database runs, so there is exactly
        // one code path and migrations are always exercised.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS sessions (
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
                CREATE INDEX IF NOT EXISTS idx_sessions_profile ON sessions(profile_name);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await MigrateAsync(connection, ct).ConfigureAwait(false);
        _initialized = true;
        _logger.LogDebug("Session store initialized (schema v{Version})", SchemaVersion);
    }

    private async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA user_version", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        // One-way change: keep a copy of the pre-migration database as cheap insurance.
        TryBackupDatabase();

        if (version < 1)
        {
            _logger.LogInformation("Migrating session store schema v{From} -> v1", version);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
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
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        if (version < 2)
        {
            _logger.LogInformation("Migrating session store schema v{From} -> v2", Math.Max(version, 1));
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                ALTER TABLE sessions ADD COLUMN stats_delta TEXT NULL;
                ALTER TABLE sessions ADD COLUMN game_version TEXT NULL;
                CREATE TABLE IF NOT EXISTS matches (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id INTEGER NULL,
                    started_at TEXT NOT NULL,
                    mode TEXT NOT NULL,
                    result TEXT NOT NULL,
                    kills INTEGER NULL,
                    deaths INTEGER NULL,
                    assists INTEGER NULL,
                    map TEXT NULL,
                    source TEXT NOT NULL DEFAULT 'manual',
                    note TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_matches_session ON matches(session_id);
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private void TryBackupDatabase()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Copy(_databasePath, _databasePath + ".bak", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not back up the session database before migration");
        }
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> SaveSessionAsync(SessionRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions (profile_name, game_package_id, started_at, duration_seconds,
                sample_count, average_fps, one_percent_low_fps, point_one_percent_low_fps,
                average_frametime_ms, p95_frametime_ms, p99_frametime_ms, fps_samples,
                tweak_ids, profile_hash, launch_kind, avg_ping_ms, jitter_ms, packet_loss_pct,
                stats_delta, game_version)
            VALUES ($profile, $package, $started, $duration, $samples, $avg, $low1, $low01, $avgFt, $p95, $p99, $fps,
                $tweaks, $hash, $kind, $ping, $jitter, $loss, $delta, $gameVersion);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$profile", record.ProfileName);
        command.Parameters.AddWithValue("$package", record.GamePackageId);
        command.Parameters.AddWithValue("$started", record.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$duration", record.Duration.TotalSeconds);
        command.Parameters.AddWithValue("$samples", record.Stats.SampleCount);
        command.Parameters.AddWithValue("$avg", record.Stats.AverageFps);
        command.Parameters.AddWithValue("$low1", record.Stats.OnePercentLowFps);
        command.Parameters.AddWithValue("$low01", record.Stats.PointOnePercentLowFps);
        command.Parameters.AddWithValue("$avgFt", record.Stats.AverageFrametimeMs);
        command.Parameters.AddWithValue("$p95", record.Stats.P95FrametimeMs);
        command.Parameters.AddWithValue("$p99", record.Stats.P99FrametimeMs);
        command.Parameters.AddWithValue("$fps",
            string.Join(',', record.FpsSamples.Select(s => s.ToString("F1", CultureInfo.InvariantCulture))));
        command.Parameters.AddWithValue("$tweaks", string.Join(',', record.TweakIds));
        command.Parameters.AddWithValue("$hash", record.ProfileHash);
        command.Parameters.AddWithValue("$kind", record.LaunchKind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$ping", record.Network is { } n1 ? n1.AveragePingMs : DBNull.Value);
        command.Parameters.AddWithValue("$jitter", record.Network is { } n2 ? n2.JitterMs : DBNull.Value);
        command.Parameters.AddWithValue("$loss", record.Network is { } n3 ? n3.PacketLossPct : DBNull.Value);
        command.Parameters.AddWithValue("$delta",
            record.StatsDelta is { } delta ? JsonSerializer.Serialize(delta) : DBNull.Value);
        command.Parameters.AddWithValue("$gameVersion", (object?)record.GameVersion ?? DBNull.Value);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        _logger.LogInformation("Session #{Id} saved ({Profile}, {Duration})", id, record.ProfileName, record.Duration);
        return id;
    }

    public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default)
        => QueryAsync("SELECT * FROM sessions ORDER BY id DESC LIMIT $limit",
            command => command.Parameters.AddWithValue("$limit", limit), ct);

    public Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default)
        => QueryAsync("SELECT * FROM sessions WHERE profile_name = $profile ORDER BY id DESC",
            command => command.Parameters.AddWithValue("$profile", profileName), ct);

    public Task<IReadOnlyList<SessionRecord>> GetSessionsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        }
        var placeholders = string.Join(',', ids.Select((_, i) => $"$id{i}"));
        return QueryAsync($"SELECT * FROM sessions WHERE id IN ({placeholders}) ORDER BY id",
            command =>
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    command.Parameters.AddWithValue($"$id{i}", ids[i]);
                }
            }, ct);
    }

    private async Task<IReadOnlyList<SessionRecord>> QueryAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var fpsText = reader.GetString(reader.GetOrdinal("fps_samples"));
            sessions.Add(new SessionRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ProfileName = reader.GetString(reader.GetOrdinal("profile_name")),
                GamePackageId = reader.GetString(reader.GetOrdinal("game_package_id")),
                StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
                Duration = TimeSpan.FromSeconds(reader.GetDouble(reader.GetOrdinal("duration_seconds"))),
                Stats = new SessionStats
                {
                    SampleCount = reader.GetInt32(reader.GetOrdinal("sample_count")),
                    AverageFps = reader.GetDouble(reader.GetOrdinal("average_fps")),
                    OnePercentLowFps = reader.GetDouble(reader.GetOrdinal("one_percent_low_fps")),
                    PointOnePercentLowFps = reader.GetDouble(reader.GetOrdinal("point_one_percent_low_fps")),
                    AverageFrametimeMs = reader.GetDouble(reader.GetOrdinal("average_frametime_ms")),
                    P95FrametimeMs = reader.GetDouble(reader.GetOrdinal("p95_frametime_ms")),
                    P99FrametimeMs = reader.GetDouble(reader.GetOrdinal("p99_frametime_ms")),
                },
                FpsSamples = fpsText.Length == 0
                    ? []
                    : fpsText.Split(',')
                        .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
                        .ToList(),
                TweakIds = ReadTweakIds(reader),
                ProfileHash = reader.GetString(reader.GetOrdinal("profile_hash")),
                LaunchKind = ParseLaunchKind(reader.GetString(reader.GetOrdinal("launch_kind"))),
                Network = ReadNetwork(reader),
                StatsDelta = ReadStatsDelta(reader),
                GameVersion = reader.IsDBNull(reader.GetOrdinal("game_version"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("game_version")),
            });
        }
        return sessions;
    }

    private static IReadOnlyList<string> ReadTweakIds(SqliteDataReader reader)
    {
        var text = reader.GetString(reader.GetOrdinal("tweak_ids"));
        return text.Length == 0 ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static LaunchKind ParseLaunchKind(string text)
        => Enum.TryParse<LaunchKind>(text, ignoreCase: true, out var kind) ? kind : LaunchKind.Play;

    private static NetworkQualityStats? ReadNetwork(SqliteDataReader reader)
    {
        var pingOrdinal = reader.GetOrdinal("avg_ping_ms");
        if (reader.IsDBNull(pingOrdinal))
        {
            return null;
        }
        return new NetworkQualityStats
        {
            AveragePingMs = reader.GetDouble(pingOrdinal),
            JitterMs = reader.GetDouble(reader.GetOrdinal("jitter_ms")),
            PacketLossPct = reader.GetDouble(reader.GetOrdinal("packet_loss_pct")),
            SampleCount = 1, // presence marker; per-sample counts are not persisted
        };
    }

    private static CopsProfileDelta? ReadStatsDelta(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("stats_delta");
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CopsProfileDelta>(reader.GetString(ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<long?> AttachStatsDeltaAsync(CopsProfileDelta delta, DateTimeOffset windowStart, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        long sessionId;
        await using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM sessions WHERE started_at >= $notBefore ORDER BY id DESC LIMIT 1";
            find.Parameters.AddWithValue("$notBefore", windowStart.ToString("O"));
            var found = await find.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is null or DBNull)
            {
                return null;
            }
            sessionId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE sessions SET stats_delta = $delta WHERE id = $id";
        update.Parameters.AddWithValue("$delta", JsonSerializer.Serialize(delta));
        update.Parameters.AddWithValue("$id", sessionId);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Stats delta attached to session #{Id} (season {Season})", sessionId, delta.Season);
        return sessionId;
    }

    public async Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO matches (session_id, started_at, mode, result, kills, deaths, assists, map, source, note)
            VALUES ($session, $started, $mode, $result, $kills, $deaths, $assists, $map, $source, $note);
            SELECT last_insert_rowid();
            """;
        BindMatch(command, match);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task UpdateMatchAsync(MatchRecord match, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE matches SET session_id = $session, started_at = $started, mode = $mode, result = $result,
                kills = $kills, deaths = $deaths, assists = $assists, map = $map, source = $source, note = $note
            WHERE id = $id
            """;
        BindMatch(command, match);
        command.Parameters.AddWithValue("$id", match.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteMatchAsync(long matchId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM matches WHERE id = $id";
        command.Parameters.AddWithValue("$id", matchId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM matches ORDER BY started_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var matches = new List<MatchRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            matches.Add(new MatchRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.IsDBNull(reader.GetOrdinal("session_id")) ? null : reader.GetInt64(reader.GetOrdinal("session_id")),
                StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
                Mode = reader.GetString(reader.GetOrdinal("mode")),
                Result = reader.GetString(reader.GetOrdinal("result")),
                Kills = ReadNullableLong(reader, "kills"),
                Deaths = ReadNullableLong(reader, "deaths"),
                Assists = ReadNullableLong(reader, "assists"),
                Map = reader.IsDBNull(reader.GetOrdinal("map")) ? null : reader.GetString(reader.GetOrdinal("map")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Note = reader.IsDBNull(reader.GetOrdinal("note")) ? null : reader.GetString(reader.GetOrdinal("note")),
            });
        }
        return matches;
    }

    private static void BindMatch(SqliteCommand command, MatchRecord match)
    {
        command.Parameters.AddWithValue("$session", (object?)match.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", match.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$mode", match.Mode);
        command.Parameters.AddWithValue("$result", match.Result);
        command.Parameters.AddWithValue("$kills", (object?)match.Kills ?? DBNull.Value);
        command.Parameters.AddWithValue("$deaths", (object?)match.Deaths ?? DBNull.Value);
        command.Parameters.AddWithValue("$assists", (object?)match.Assists ?? DBNull.Value);
        command.Parameters.AddWithValue("$map", (object?)match.Map ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", match.Source);
        command.Parameters.AddWithValue("$note", (object?)match.Note ?? DBNull.Value);
    }

    private static long? ReadNullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
        }
    }
}
