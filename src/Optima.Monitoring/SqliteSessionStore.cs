using System.Globalization;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Optima.Monitoring;

/// <summary>Session history in SQLite (%LOCALAPPDATA%\Optima\sessions.db, §13/§21).</summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionStore> _logger;
    private bool _initialized;

    public SqliteSessionStore(AppPaths paths, ILogger<SqliteSessionStore> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.SessionsDatabase }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
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
        _initialized = true;
        _logger.LogDebug("Session store initialized");
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
                average_frametime_ms, p95_frametime_ms, p99_frametime_ms, fps_samples)
            VALUES ($profile, $package, $started, $duration, $samples, $avg, $low1, $low01, $avgFt, $p95, $p99, $fps);
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
            });
        }
        return sessions;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            await InitializeAsync(ct).ConfigureAwait(false);
        }
    }
}
