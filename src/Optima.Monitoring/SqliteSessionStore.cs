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
    private const int SchemaVersion = 3;

    /// <summary>Every column of a session row, spelled out so a query never pulls a series it does not need.</summary>
    private const string SessionColumns =
        "id, profile_name, game_package_id, started_at, duration_seconds, sample_count, average_fps, " +
        "one_percent_low_fps, point_one_percent_low_fps, average_frametime_ms, p95_frametime_ms, " +
        "p99_frametime_ms, tweak_ids, profile_hash, launch_kind, avg_ping_ms, jitter_ms, packet_loss_pct, " +
        "stats_delta, game_version, stats_baseline, fps_samples";

    /// <summary>The same list without the per-second fps series: the history list shows numbers, not graphs.</summary>
    private const string SessionSummaryColumns =
        "id, profile_name, game_package_id, started_at, duration_seconds, sample_count, average_fps, " +
        "one_percent_low_fps, point_one_percent_low_fps, average_frametime_ms, p95_frametime_ms, " +
        "p99_frametime_ms, tweak_ids, profile_hash, launch_kind, avg_ping_ms, jitter_ms, packet_loss_pct, " +
        "stats_delta, game_version, stats_baseline";

    private const string MatchColumns =
        "id, session_id, started_at, mode, result, kills, deaths, assists, map, source, note";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionStore> _logger;

    // One gate for the whole "create the schema once" step: two callers racing on _initialized
    // used to run the migration twice, and a losing ALTER TABLE throws.
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteSessionStore(AppPaths paths, ILogger<SqliteSessionStore> logger)
    {
        _databasePath = paths.SessionsDatabase;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.SessionsDatabase }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            await InitializeCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Opened without the journal pragmas. A database this app has opened before is already in
        // write-ahead mode (the setting is stored in the file), so recent commits may live only in
        // the -wal file: fold them into the main file before anything copies it as a backup.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteScalarAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", ct).ConfigureAwait(false);

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

        // Write-ahead logging plus relaxed syncing: the store is written once per session and read
        // from the UI, so readers should never block behind a writer, and a flush per commit buys
        // nothing for a file this process owns. Set after the migrations, so the pre-migration
        // backup is a plain single-file copy.
        await ApplyJournalPragmasAsync(connection, ct).ConfigureAwait(false);

        _initialized = true;
        _logger.LogDebug("Session store initialized (schema v{Version})", SchemaVersion);
    }

    private static async Task ApplyJournalPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens a connection with the pragmas every statement relies on: a writer waits rather than failing, and readers take the log path.</summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ApplyJournalPragmasAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Column ordinals for one result set. GetOrdinal walks the column list on every call, and the
    /// session readers asked for one per column per row; each name is resolved once per query here.
    /// </summary>
    private sealed class ColumnOrdinals
    {
        private readonly Dictionary<string, int> _ordinals = new(StringComparer.OrdinalIgnoreCase);

        public int Get(SqliteDataReader reader, string column)
        {
            if (_ordinals.TryGetValue(column, out var ordinal))
            {
                return ordinal;
            }
            ordinal = reader.GetOrdinal(column);
            _ordinals[column] = ordinal;
            return ordinal;
        }
    }

    private async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = Convert.ToInt32(await ExecuteScalarAsync(connection, "PRAGMA user_version", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

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

        if (version < 3)
        {
            _logger.LogInformation("Migrating session store schema v{From} -> v3", Math.Max(version, 2));
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                ALTER TABLE sessions ADD COLUMN stats_baseline TEXT NULL;
                PRAGMA user_version = 3;
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
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions (profile_name, game_package_id, started_at, duration_seconds,
                sample_count, average_fps, one_percent_low_fps, point_one_percent_low_fps,
                average_frametime_ms, p95_frametime_ms, p99_frametime_ms, fps_samples,
                tweak_ids, profile_hash, launch_kind, avg_ping_ms, jitter_ms, packet_loss_pct,
                stats_delta, game_version, stats_baseline)
            VALUES ($profile, $package, $started, $duration, $samples, $avg, $low1, $low01, $avgFt, $p95, $p99, $fps,
                $tweaks, $hash, $kind, $ping, $jitter, $loss, $delta, $gameVersion, $baseline);
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
        command.Parameters.AddWithValue("$baseline",
            record.StatsBaseline is { } baseline ? JsonSerializer.Serialize(baseline) : DBNull.Value);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        _logger.LogInformation("Session #{Id} saved ({Profile}, {Duration})", id, record.ProfileName, record.Duration);
        return id;
    }

    public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int limit = 50, CancellationToken ct = default)
        => QueryAsync($"SELECT {SessionColumns} FROM sessions ORDER BY id DESC LIMIT $limit",
            command => command.Parameters.AddWithValue("$limit", limit), ct);

    public Task<IReadOnlyList<SessionRecord>> GetSessionSummariesAsync(int limit = 50, CancellationToken ct = default)
        => QueryAsync($"SELECT {SessionSummaryColumns} FROM sessions ORDER BY id DESC LIMIT $limit",
            command => command.Parameters.AddWithValue("$limit", limit), ct, withSamples: false);

    public Task<IReadOnlyList<SessionRecord>> GetSessionsByProfileAsync(string profileName, CancellationToken ct = default)
        => QueryAsync($"SELECT {SessionColumns} FROM sessions WHERE profile_name = $profile ORDER BY id DESC",
            command => command.Parameters.AddWithValue("$profile", profileName), ct);

    public Task<IReadOnlyList<SessionRecord>> GetSessionsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        }
        var placeholders = string.Join(',', ids.Select((_, i) => $"$id{i}"));
        return QueryAsync($"SELECT {SessionColumns} FROM sessions WHERE id IN ({placeholders}) ORDER BY id",
            command =>
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    command.Parameters.AddWithValue($"$id{i}", ids[i]);
                }
            }, ct);
    }

    private async Task<IReadOnlyList<SessionRecord>> QueryAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken ct, bool withSamples = true)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var columns = new ColumnOrdinals();
        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader, columns, withSamples));
        }
        return sessions;
    }

    private static SessionRecord ReadSession(SqliteDataReader reader, ColumnOrdinals columns, bool withSamples)
    {
        IReadOnlyList<double> samples = [];
        if (withSamples)
        {
            var text = reader.GetString(columns.Get(reader, "fps_samples"));
            samples = text.Length == 0
                ? []
                : text.Split(',')
                    .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
                    .ToList();
        }

        return new SessionRecord
        {
            Id = reader.GetInt64(columns.Get(reader, "id")),
            ProfileName = reader.GetString(columns.Get(reader, "profile_name")),
            GamePackageId = reader.GetString(columns.Get(reader, "game_package_id")),
            StartedAt = DateTimeOffset.Parse(reader.GetString(columns.Get(reader, "started_at")), CultureInfo.InvariantCulture),
            Duration = TimeSpan.FromSeconds(reader.GetDouble(columns.Get(reader, "duration_seconds"))),
            Stats = new SessionStats
            {
                SampleCount = reader.GetInt32(columns.Get(reader, "sample_count")),
                AverageFps = reader.GetDouble(columns.Get(reader, "average_fps")),
                OnePercentLowFps = reader.GetDouble(columns.Get(reader, "one_percent_low_fps")),
                PointOnePercentLowFps = reader.GetDouble(columns.Get(reader, "point_one_percent_low_fps")),
                AverageFrametimeMs = reader.GetDouble(columns.Get(reader, "average_frametime_ms")),
                P95FrametimeMs = reader.GetDouble(columns.Get(reader, "p95_frametime_ms")),
                P99FrametimeMs = reader.GetDouble(columns.Get(reader, "p99_frametime_ms")),
            },
            FpsSamples = samples,
            TweakIds = ReadTweakIds(reader, columns),
            ProfileHash = reader.GetString(columns.Get(reader, "profile_hash")),
            LaunchKind = ParseLaunchKind(reader.GetString(columns.Get(reader, "launch_kind"))),
            Network = ReadNetwork(reader, columns),
            StatsDelta = ReadStatsDelta(reader, columns),
            StatsBaseline = ReadStatsBaseline(reader, columns),
            GameVersion = reader.IsDBNull(columns.Get(reader, "game_version"))
                ? null
                : reader.GetString(columns.Get(reader, "game_version")),
        };
    }

    private static IReadOnlyList<string> ReadTweakIds(SqliteDataReader reader, ColumnOrdinals columns)
    {
        var text = reader.GetString(columns.Get(reader, "tweak_ids"));
        return text.Length == 0 ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static LaunchKind ParseLaunchKind(string text)
        => Enum.TryParse<LaunchKind>(text, ignoreCase: true, out var kind) ? kind : LaunchKind.Play;

    private static NetworkQualityStats? ReadNetwork(SqliteDataReader reader, ColumnOrdinals columns)
    {
        var pingOrdinal = columns.Get(reader, "avg_ping_ms");
        if (reader.IsDBNull(pingOrdinal))
        {
            return null;
        }
        return new NetworkQualityStats
        {
            AveragePingMs = reader.GetDouble(pingOrdinal),
            JitterMs = reader.GetDouble(columns.Get(reader, "jitter_ms")),
            PacketLossPct = reader.GetDouble(columns.Get(reader, "packet_loss_pct")),
            SampleCount = 1,
        };
    }

    private static CopsProfileDelta? ReadStatsDelta(SqliteDataReader reader, ColumnOrdinals columns)
    {
        var ordinal = columns.Get(reader, "stats_delta");
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

    private static CopsSeasonStats? ReadStatsBaseline(SqliteDataReader reader, ColumnOrdinals columns)
    {
        var ordinal = columns.Get(reader, "stats_baseline");
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CopsSeasonStats>(reader.GetString(ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<long?> AttachStatsAsync(CopsProfileDelta? delta, CopsSeasonStats? baseline,
        DateTimeOffset windowStart, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

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

        // COALESCE everywhere: an empty delta (the API had not published the match yet) never erases a
        // delta the row already carries, and a baseline never replaces one already on file. The second
        // rule is what keeps the window's fallback row - the previous session, when a run never saved
        // its own - from losing the snapshot it needs to be refreshable itself.
        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE sessions SET stats_delta = COALESCE($delta, stats_delta), " +
            "stats_baseline = COALESCE(stats_baseline, $baseline) WHERE id = $id";
        update.Parameters.AddWithValue("$delta", delta is null ? DBNull.Value : JsonSerializer.Serialize(delta));
        update.Parameters.AddWithValue("$baseline", baseline is null ? DBNull.Value : JsonSerializer.Serialize(baseline));
        update.Parameters.AddWithValue("$id", sessionId);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Stats attached to session #{Id} (season {Season}, delta: {Delta})",
            sessionId, baseline?.Season ?? delta?.Season ?? 0, delta is null ? "none yet" : "recorded");
        return sessionId;
    }

    public async Task<bool> UpdateStatsDeltaAsync(long sessionId, CopsProfileDelta delta, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET stats_delta = $delta WHERE id = $id";
        command.Parameters.AddWithValue("$delta", JsonSerializer.Serialize(delta));
        command.Parameters.AddWithValue("$id", sessionId);
        var updated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Stats delta for session #{Id} refreshed from the API (rows: {Rows})", sessionId, updated);
        return updated > 0;
    }

    public async Task<SessionEndBoundary> GetSessionEndBoundaryAsync(long sessionId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Ids are AUTOINCREMENT, so the next id is the next run in time.
        command.CommandText = "SELECT stats_baseline FROM sessions WHERE id > $id ORDER BY id ASC LIMIT 1";
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new SessionEndBoundary(HasNextSession: false, Baseline: null);
        }
        var ordinal = reader.GetOrdinal("stats_baseline");
        if (reader.IsDBNull(ordinal))
        {
            return new SessionEndBoundary(HasNextSession: true, Baseline: null);
        }
        try
        {
            return new SessionEndBoundary(true, JsonSerializer.Deserialize<CopsSeasonStats>(reader.GetString(ordinal)));
        }
        catch (JsonException)
        {
            return new SessionEndBoundary(HasNextSession: true, Baseline: null);
        }
    }

    public async Task<long> SaveMatchAsync(MatchRecord match, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM matches WHERE id = $id";
        command.Parameters.AddWithValue("$id", matchId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MatchRecord>> GetMatchesAsync(int limit = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MatchColumns} FROM matches ORDER BY started_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var columns = new ColumnOrdinals();
        var matches = new List<MatchRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sessionId = columns.Get(reader, "session_id");
            var map = columns.Get(reader, "map");
            var note = columns.Get(reader, "note");
            matches.Add(new MatchRecord
            {
                Id = reader.GetInt64(columns.Get(reader, "id")),
                SessionId = reader.IsDBNull(sessionId) ? null : reader.GetInt64(sessionId),
                StartedAt = DateTimeOffset.Parse(reader.GetString(columns.Get(reader, "started_at")), CultureInfo.InvariantCulture),
                Mode = reader.GetString(columns.Get(reader, "mode")),
                Result = reader.GetString(columns.Get(reader, "result")),
                Kills = ReadNullableLong(reader, columns, "kills"),
                Deaths = ReadNullableLong(reader, columns, "deaths"),
                Assists = ReadNullableLong(reader, columns, "assists"),
                Map = reader.IsDBNull(map) ? null : reader.GetString(map),
                Source = reader.GetString(columns.Get(reader, "source")),
                Note = reader.IsDBNull(note) ? null : reader.GetString(note),
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

    private static long? ReadNullableLong(SqliteDataReader reader, ColumnOrdinals columns, string column)
    {
        var ordinal = columns.Get(reader, column);
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
