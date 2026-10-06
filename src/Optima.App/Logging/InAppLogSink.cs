using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Optima.Core.Health;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Optima.App.Logging;

/// <summary>
/// One row of the log list. The row shows a line; the record behind it keeps the whole exception,
/// the code Windows returned and the message's arguments for the detail pane and a copied report.
/// </summary>
public sealed record LogEntry(LogRecord Record)
{
    public DateTimeOffset Timestamp => Record.Timestamp;

    public string Level => Record.LevelText;

    public string Source => Record.ShortSource;

    public string Message => Record.Line;

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    /// <summary>The row has more behind it than its line shows: an exception or an error code.</summary>
    public bool HasDetail => Record.Exception is not null || Record.ErrorCode.Length > 0;
}

/// <summary>
/// The log lines behind the LOGS page. A subclass only so that the trim can drop a whole chunk of
/// old lines in a single change notification: removing them one by one raised a removal per line,
/// and the virtualizing panel had to process each of them.
/// </summary>
public sealed class LogEntryCollection : ObservableCollection<LogEntry>
{
    public void DropOldest(int count)
    {
        count = Math.Min(count, Count);
        if (count <= 0)
        {
            return;
        }

        var kept = new LogEntry[Count - count];
        for (var i = count; i < Count; i++)
        {
            kept[i - count] = this[i];
        }

        Items.Clear();
        foreach (var entry in kept)
        {
            Items.Add(entry);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Serilog sink feeding the in-app log viewer (§17).</summary>
public sealed class InAppLogSink : ILogEventSink
{
    private const int MaxEntries = 2000;

    /// <summary>
    /// How far past the cap the list may grow before a trim. Trimming once per arriving entry meant
    /// one collection change per entry at steady state; trimming per batch means one per chunk.
    /// </summary>
    private const int TrimBatch = 250;

    /// <summary>
    /// How often queued lines reach the view. The page is a readable list rather than a live
    /// waveform, so a log storm costs a handful of UI updates a second instead of one per line.
    /// </summary>
    private const int FlushIntervalMs = 150;

    /// <summary>
    /// Ceiling on ordinary lines waiting for the view, so a flood behind a busy UI thread stays
    /// bounded. Warnings and errors get twice that before they are dropped too: a flood is made of
    /// trace lines, and the one error in the middle of it is the line that must arrive.
    /// </summary>
    private const int MaxQueued = 8000;

    /// <summary>
    /// Literal rendering. Serilog's own RenderMessage puts every string argument in quotes, which
    /// is how a phase message ended up on the page as [Failed] "Unexpected error".
    /// </summary>
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:l}");

    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly Timer _flushTimer;
    private int _flushScheduled;

    public InAppLogSink()
    {
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LogEntryCollection Entries { get; } = [];

    /// <summary>
    /// Runs on whatever thread logged, the UI thread and the launch pipeline included, so it only
    /// maps and queues. A line written while no dispatcher exists (before the first window, during
    /// shutdown) waits in the queue instead of being thrown away.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        var queued = _incoming.Count;
        if (queued >= MaxQueued && (logEvent.Level < LogEventLevel.Warning || queued >= MaxQueued * 2))
        {
            return;
        }

        _incoming.Enqueue(new LogEntry(ToRecord(logEvent)));

        // One timer per burst: whoever arrives first arms it, the rest just join the queue.
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _flushTimer.Change(FlushIntervalMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Drain();
            return;
        }

        // Background priority: log lines never compete with input or animation for the UI thread.
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        var batch = new List<LogEntry>();
        while (_incoming.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }
        if (batch.Count == 0)
        {
            return;
        }

        var excess = Entries.Count + batch.Count - (MaxEntries + TrimBatch);
        if (excess > 0)
        {
            Entries.DropOldest(excess);
        }
        foreach (var entry in batch)
        {
            Entries.Add(entry);
        }
    }

    private static LogRecord ToRecord(LogEvent logEvent)
    {
        var exception = logEvent.Exception is { } thrown ? ExceptionDetail.Capture(thrown) : null;
        return new LogRecord
        {
            Timestamp = logEvent.Timestamp,
            Level = ToLevel(logEvent.Level),
            Source = SourceContext(logEvent),
            Message = Render(logEvent),
            Template = logEvent.MessageTemplate.Text,
            Exception = exception,
            Properties = exception is not null || logEvent.Level >= LogEventLevel.Warning
                ? CopyProperties(logEvent)
                : LogRecord.NoProperties,
        };
    }

    private static string Render(LogEvent logEvent)
    {
        using var writer = new System.IO.StringWriter();
        MessageFormatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogLevel ToLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    private static string SourceContext(LogEvent logEvent)
        => logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context }
            ? context
            : string.Empty;

    private static IReadOnlyDictionary<string, string> CopyProperties(LogEvent logEvent)
    {
        var copy = new Dictionary<string, string>(logEvent.Properties.Count);
        foreach (var (name, value) in logEvent.Properties)
        {
            if (name == "SourceContext")
            {
                continue;
            }
            // A string keeps its own text; anything else is written the way Serilog would write it.
            copy[name] = value is ScalarValue { Value: string text } ? text : value.ToString();
        }
        return copy.Count == 0 ? LogRecord.NoProperties : copy;
    }
}
