using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using Serilog.Core;
using Serilog.Events;

namespace Optima.App.Logging;

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
}

/// <summary>
/// Serilog sink feeding the in-app log viewer (§17). Keeps a bounded buffer on the UI thread's
/// dispatcher so the LOGS page can bind directly to it.
/// </summary>
public sealed class InAppLogSink : ILogEventSink
{
    private const int MaxEntries = 2000;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp,
            ShortLevel(logEvent.Level),
            SourceName(logEvent),
            logEvent.RenderMessage() + (logEvent.Exception is { } ex ? $" — {ex.GetType().Name}: {ex.Message}" : string.Empty));

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }
        });
    }

    private static string ShortLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRACE",
        LogEventLevel.Debug => "DEBUG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARN",
        LogEventLevel.Error => "ERROR",
        LogEventLevel.Fatal => "CRITICAL",
        _ => level.ToString().ToUpperInvariant(),
    };

    private static string SourceName(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context })
        {
            var lastDot = context.LastIndexOf('.');
            return lastDot >= 0 ? context[(lastDot + 1)..] : context;
        }
        return string.Empty;
    }
}

/// <summary>Masks anything token-shaped before logs leave the machine (§17).</summary>
public static partial class LogRedactor
{
    [GeneratedRegex(@"(?i)(token|bearer|password|secret|api[_-]?key)\s*[=:]\s*\S+")]
    private static partial Regex SecretPattern();

    public static string Redact(string text) => SecretPattern().Replace(text, "$1=[REDACTED]");
}
