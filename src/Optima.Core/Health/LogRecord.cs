using Microsoft.Extensions.Logging;

namespace Optima.Core.Health;

/// <summary>
/// One log line with everything it carried when it was written. The log list shows a line of it;
/// the detail pane, the export and a copied report work from the rest.
/// </summary>
public sealed record LogRecord
{
    public static IReadOnlyDictionary<string, string> NoProperties { get; } = new Dictionary<string, string>();

    public required DateTimeOffset Timestamp { get; init; }

    public required LogLevel Level { get; init; }

    /// <summary>The full name of what wrote the line; empty for the app's own startup and shutdown lines.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The message as written, without the exception.</summary>
    public required string Message { get; init; }

    /// <summary>The message before its arguments were filled in: the same for every line one call site writes.</summary>
    public string Template { get; init; } = string.Empty;

    public ExceptionDetail? Exception { get; init; }

    /// <summary>
    /// The message's named arguments. Kept for warnings and above and for anything with an
    /// exception; the lines a busy log is made of do not pay for a copy nobody will read.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = NoProperties;

    public string ShortSource
    {
        get
        {
            var lastDot = Source.LastIndexOf('.');
            return lastDot >= 0 ? Source[(lastDot + 1)..] : Source;
        }
    }

    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => Level.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// The Optima error code the line is about: the exception's own, else a {Code} argument of the
    /// message. Empty when the line has neither.
    /// </summary>
    public string ErrorCode => Exception is { ErrorCode.Length: > 0 } exception
        ? exception.ErrorCode
        : Properties.TryGetValue("Code", out var code) ? code : string.Empty;

    /// <summary>
    /// The line as the log list shows it: the message, and the short form of the exception. A
    /// message that already says what the exception says is not followed by it a second time; a
    /// Windows error code always is, because the message never carries that.
    /// </summary>
    public string Line => Exception is null
        || (Exception.NativeErrorCode is null && Message.Contains(Exception.Message, StringComparison.Ordinal))
        ? Message
        : $"{Message} ({Exception.Summary})";
}
