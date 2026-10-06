using Microsoft.Extensions.Logging;
using Optima.Core.Abstractions;
using Optima.Core.Models;

namespace Optima.Core.Health;

/// <summary>
/// Keeps the list of what is wrong. It reads two things: every log record as it is written, and
/// the result of every check that is run. Nobody has to open the log to find out that something
/// failed; the failure is on the list, once, with its evidence, however many lines it wrote.
///
/// It detects and lists. It changes nothing on the system.
/// </summary>
public sealed class IssueEngine : IDisposable
{
    /// <summary>
    /// How many unrecognised errors are listed one by one. A storm of distinct failures past that
    /// is counted under a single overflow issue instead of burying the list and the memory.
    /// </summary>
    public const int MaxUnclassified = 50;

    public const string OverflowCode = "ISSUES_OVERFLOW";

    /// <summary>How many log lines before an issue's first occurrence are kept with it.</summary>
    private const int LeadUpLines = 20;

    /// <summary>The first occurrence is always kept; of the rest, the most recent this many.</summary>
    private const int LatestOccurrences = 5;

    private readonly IssueStateFile _state;
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<IssueEngine> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _notifyDelay;

    private readonly object _gate = new();
    private readonly Dictionary<string, Issue> _issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IgnoredIssue> _ignored = new(StringComparer.Ordinal);
    private readonly Queue<LogRecord> _recent = new();
    private readonly Timer _notifyTimer;
    private int _notifyScheduled;
    private CancellationTokenSource? _cts;

    /// <param name="startupDelay">How long after <see cref="Start"/> the startup checks run; the app gets to finish starting first.</param>
    /// <param name="notifyDelay">Changes are announced at most once per this long. Zero announces each at once, for tests.</param>
    public IssueEngine(
        IssueStateFile state,
        IEnumerable<IDiagnosticCheck> checks,
        ILogger<IssueEngine> logger,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? startupDelay = null,
        TimeSpan? notifyDelay = null)
    {
        _state = state;
        _checks = checks.OrderBy(c => c.Order).ToList();
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(6);
        _notifyDelay = notifyDelay ?? TimeSpan.FromMilliseconds(500);
        _notifyTimer = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Raised after the list changed, on whatever thread noticed, and not more often than a couple
    /// of times a second: a failure that repeats must not repaint the page for every repeat.
    /// </summary>
    public event Action? Changed;

    /// <summary>The open issues, the most serious first and the most recent first among equals.</summary>
    public IReadOnlyList<Issue> Issues
    {
        get
        {
            lock (_gate)
            {
                return _issues.Values
                    .OrderByDescending(i => i.Severity)
                    .ThenByDescending(i => i.LastSeen)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<IgnoredIssue> Ignored
    {
        get
        {
            lock (_gate)
            {
                return _ignored.Values.OrderByDescending(i => i.At).ToList();
            }
        }
    }

    /// <summary>Loads what the user decided in earlier runs, and runs the startup checks once the app has settled.</summary>
    public void Start()
    {
        var saved = _state.Load();
        lock (_gate)
        {
            foreach (var ignored in saved.Ignored)
            {
                _ignored[ignored.Key] = ignored;
            }
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_startupDelay, ct).ConfigureAwait(false);
                await ScanAsync(CheckScope.Startup, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
    }

    /// <summary>
    /// Takes one log record. Called for every line on whatever thread wrote it, so the common case,
    /// a line that is not an issue, costs a comparison and a place in the lead-up buffer.
    /// </summary>
    public void Ingest(LogRecord record)
    {
        var changed = false;
        Issue? raised = null;
        lock (_gate)
        {
            var match = LogRuleTable.Classify(record);
            if (match is not null && !IsAboutOwnFile(record) && !_ignored.ContainsKey(match.Key))
            {
                raised = Record(match, record);
                changed = true;
            }

            _recent.Enqueue(record);
            while (_recent.Count > LeadUpLines)
            {
                _recent.Dequeue();
            }
        }
        if (changed)
        {
            Announce(raised);
        }
    }

    /// <summary>
    /// Takes the result of a check that stands for an issue. A failure opens the issue; a pass
    /// closes it, whichever way it was opened, because the check is the proof that it is gone.
    /// </summary>
    public void Report(DiagnosticResult result)
    {
        if (result.IssueCode is not { Length: > 0 } code || result.Status == DiagnosticStatus.Skipped)
        {
            return;
        }

        var changed = false;
        Issue? raised = null;
        lock (_gate)
        {
            if (result.Status == DiagnosticStatus.Pass)
            {
                changed = _issues.Remove(code);
            }
            else if (!_ignored.ContainsKey(code))
            {
                var now = _clock();
                var reported = result.Status == DiagnosticStatus.Fail ? IssueSeverity.Error : IssueSeverity.Warning;
                var detail = result.RecommendedFix.Length > 0 ? $"{result.Reason} {result.RecommendedFix}" : result.Reason;
                if (_issues.TryGetValue(code, out var open))
                {
                    // A check that still fails is the same occurrence seen again, not another one.
                    _issues[code] = open with { LastSeen = now, Detail = detail };
                }
                else
                {
                    raised = _issues[code] = new Issue
                    {
                        Key = code,
                        Code = code,
                        Severity = LogRuleTable.SeverityFor(code, reported),
                        Title = ErrorCatalog.Find(code)?.Title ?? result.CheckName,
                        Detail = detail,
                        FirstSeen = now,
                        LastSeen = now,
                    };
                }
                changed = true;
            }
        }
        if (changed)
        {
            Announce(raised);
        }
    }

    /// <summary>Runs the checks of a scope and reports each result. A check that throws is skipped, not fatal.</summary>
    public async Task ScanAsync(CheckScope scope, CancellationToken ct = default)
    {
        foreach (var check in _checks)
        {
            if (scope != CheckScope.OnDemand && !check.Scope.HasFlag(scope))
            {
                continue;
            }
            ct.ThrowIfCancellationRequested();
            try
            {
                Report(await check.RunAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // From this namespace, so the line is not read back in as an issue about the scan.
                _logger.LogWarning(ex, "Check {Check} could not run", check.Name);
            }
        }
    }

    /// <summary>Takes an issue off the list for now. It comes back if it happens again.</summary>
    public void Dismiss(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _issues.Remove(key);
        }
        if (removed)
        {
            Notify();
        }
    }

    /// <summary>Takes an issue off the list for good: it is not raised again, in this run or a later one.</summary>
    public Task IgnoreAsync(string key)
    {
        IssueStateData data;
        lock (_gate)
        {
            var title = _issues.TryGetValue(key, out var issue) ? issue.Title : key;
            _issues.Remove(key);
            _ignored[key] = new IgnoredIssue(key, title, _clock());
            data = Snapshot();
        }
        Notify();
        return _state.SaveAsync(data);
    }

    public Task StopIgnoringAsync(string key)
    {
        IssueStateData data;
        lock (_gate)
        {
            if (!_ignored.Remove(key))
            {
                return Task.CompletedTask;
            }
            data = Snapshot();
        }
        Notify();
        return _state.SaveAsync(data);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _notifyTimer.Dispose();
    }

    private IssueStateData Snapshot() => new() { Ignored = [.. _ignored.Values] };

    /// <summary>
    /// Says in the log that an issue was raised, then tells the listeners. Never called with the
    /// gate held: a log line written under it would come straight back in through
    /// <see cref="Ingest"/> on another thread's lock order and could stop both.
    /// </summary>
    private void Announce(Issue? raised)
    {
        if (raised is not null)
        {
            _logger.LogInformation("Issue raised: {Code} ({Severity}) {Title}", raised.Code, raised.Severity.ToString(), raised.Title);
        }
        Notify();
    }

    /// <returns>The issue when this record opened it; null when it was one more occurrence of an open one.</returns>
    private Issue? Record(IssueMatch match, LogRecord record)
    {
        if (!_issues.ContainsKey(match.Key)
            && match.Code == LogRuleTable.Unclassified
            && _issues.Values.Count(i => i.Code == LogRuleTable.Unclassified) >= MaxUnclassified)
        {
            match = new IssueMatch(OverflowCode, OverflowCode, IssueSeverity.Error,
                ErrorCatalog.Find(OverflowCode)?.Title ?? "More errors than the list can show", match.Detail);
        }

        if (_issues.TryGetValue(match.Key, out var open))
        {
            // The first occurrence stays; behind it, the most recent few.
            var later = open.Evidence.Skip(1).Append(record).TakeLast(LatestOccurrences);
            _issues[match.Key] = open with
            {
                Count = open.Count + 1,
                LastSeen = record.Timestamp,
                Detail = match.Detail,
                Evidence = [.. open.Evidence.Take(1), .. later],
            };
            return null;
        }

        return _issues[match.Key] = new Issue
        {
            Key = match.Key,
            Code = match.Code,
            Severity = match.Severity,
            Title = match.Title,
            Detail = match.Detail,
            FirstSeen = record.Timestamp,
            LastSeen = record.Timestamp,
            Evidence = [record],
            LeadUp = [.. _recent],
        };
    }

    /// <summary>
    /// The store that keeps this engine's own file logs when that file is damaged. Raising an issue
    /// about it would have the engine saving, and failing to save, a list about itself.
    /// </summary>
    private bool IsAboutOwnFile(LogRecord record)
        => record.Properties.TryGetValue("Path", out var path)
            && string.Equals(path, _state.Path, StringComparison.OrdinalIgnoreCase);

    private void Notify()
    {
        if (_notifyDelay <= TimeSpan.Zero)
        {
            RaiseChanged();
            return;
        }
        // One timer per burst: whoever changes the list first arms it, the rest ride along.
        if (Interlocked.Exchange(ref _notifyScheduled, 1) == 0)
        {
            try
            {
                _notifyTimer.Change(_notifyDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _notifyScheduled, 0);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A listener that throws must not take the logging thread that got here with it.
            _logger.LogDebug(ex, "An issue list listener failed");
        }
    }
}
