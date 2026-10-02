using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Serilog.Core;
using Serilog.Events;

namespace Optima.App.Logging;

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
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

    /// <summary>Ceiling on lines waiting for the view, so a flood behind a busy UI thread stays bounded.</summary>
    private const int MaxQueued = 8000;

    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly Timer _flushTimer;
    private int _flushScheduled;

    public InAppLogSink()
    {
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LogEntryCollection Entries { get; } = [];

    public void Emit(LogEvent logEvent)
    {
        if (Application.Current is null)
        {
            return;
        }

        if (_incoming.Count >= MaxQueued)
        {
            return;
        }

        _incoming.Enqueue(new LogEntry(
            logEvent.Timestamp,
            ShortLevel(logEvent.Level),
            SourceName(logEvent),
            logEvent.RenderMessage() + (logEvent.Exception is { } ex ? $" ({ex.GetType().Name}: {ex.Message})" : string.Empty)));

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
