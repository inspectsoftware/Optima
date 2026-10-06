using System.Text.Json;

namespace Optima.Core.Health;

/// <summary>A fatal error Optima wrote down on its way out.</summary>
public sealed record FatalRecord(DateTimeOffset At, string Origin, string Summary, string FullText);

/// <summary>What the previous run of Optima left behind for this one to find.</summary>
public sealed record PreviousRun
{
    public static PreviousRun Clean { get; } = new();

    /// <summary>
    /// The last run never reached its normal exit: it crashed hard, was killed, or the PC lost
    /// power. Whatever it had changed on the system may still be changed.
    /// </summary>
    public bool EndedUncleanly { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string Version { get; init; } = string.Empty;

    /// <summary>The fatal error that run recorded, when it got the chance to.</summary>
    public FatalRecord? Fatal { get; init; }
}

/// <summary>
/// Two small files that let a run say how the one before it ended. A run marker is written at
/// start and removed at a normal exit, so finding one means the last run did not end normally. A
/// fatal record is written synchronously from the unhandled-exception handlers: the log is the
/// first thing to be lost when a process dies, and this is what survives it.
///
/// Nothing here throws. It runs first at startup and last in a crash, where a second failure
/// would hide the first.
/// </summary>
public sealed class CrashMarker
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _runFile;
    private readonly string _fatalFile;

    public CrashMarker(string directory)
    {
        _runFile = Path.Combine(directory, "run.json");
        _fatalFile = Path.Combine(directory, "fatal.json");
    }

    private sealed record RunRecord(DateTimeOffset StartedAt, string Version);

    /// <summary>Reads what the previous run left, clears it, and marks this run as started.</summary>
    public PreviousRun Begin(string version, DateTimeOffset now)
    {
        var run = Read<RunRecord>(_runFile);
        var fatal = Read<FatalRecord>(_fatalFile);
        Delete(_fatalFile);
        Write(_runFile, new RunRecord(now, version));

        return run is null && fatal is null
            ? PreviousRun.Clean
            : new PreviousRun
            {
                EndedUncleanly = run is not null,
                StartedAt = run?.StartedAt,
                Version = run?.Version ?? string.Empty,
                Fatal = fatal,
            };
    }

    /// <summary>Writes the fatal error down before the process goes. The first one of a run is the one that is kept.</summary>
    public void RecordFatal(string origin, Exception exception, DateTimeOffset now)
    {
        if (File.Exists(_fatalFile))
        {
            return;
        }
        var detail = ExceptionDetail.Capture(exception);
        Write(_fatalFile, new FatalRecord(now, origin, detail.Summary, detail.FullText));
    }

    /// <summary>A normal exit: the next run finds no run marker.</summary>
    public void End() => Delete(_runFile);

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
