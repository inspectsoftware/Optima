using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Optima.Core.Health;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>One issue as a card. The card outlives a refresh, so an open evidence expander stays open while the count climbs.</summary>
public sealed partial class IssueCard : ObservableObject
{
    public IssueCard(Issue issue)
    {
        _issue = issue;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(SeverityTag))]
    [NotifyPropertyChangedFor(nameof(SeverityLevel))]
    [NotifyPropertyChangedFor(nameof(SeenText))]
    [NotifyPropertyChangedFor(nameof(EvidenceText))]
    [NotifyPropertyChangedFor(nameof(HasEvidence))]
    private Issue _issue;

    public string Key => Issue.Key;

    public string Code => Issue.Code;

    public string Title => Issue.Title;

    /// <summary>Redacted on screen for the same reason the log detail is: a screenshot is how it travels.</summary>
    public string Detail => Redactor.Redact(Issue.Detail);

    public string SeverityTag => Issue.Severity switch
    {
        IssueSeverity.Critical => "CRITICAL",
        IssueSeverity.Error => "ERROR",
        IssueSeverity.Warning => "WARN",
        _ => "NOTE",
    };

    /// <summary>The severity in the log's level names, which is what the level brush converter reads.</summary>
    public string SeverityLevel => Issue.Severity switch
    {
        IssueSeverity.Critical => "CRITICAL",
        IssueSeverity.Error => "ERROR",
        IssueSeverity.Warning => "WARN",
        _ => "INFO",
    };

    public string SeenText
    {
        get
        {
            var first = Issue.FirstSeen.ToLocalTime();
            var last = Issue.LastSeen.ToLocalTime();
            var day = first.Date == DateTime.Today ? "HH:mm:ss" : "yyyy-MM-dd HH:mm";
            return Issue.Count > 1
                ? $"seen {Issue.Count} times · first {first.ToString(day)} · last {last.ToString(day)}"
                : last - first > TimeSpan.FromMinutes(1)
                    ? $"since {first.ToString(day)} · still there at {last.ToString(day)}"
                    : $"seen at {first.ToString(day)}";
        }
    }

    /// <summary>The error guide's entry: what it is, why it happens, how to put it right.</summary>
    public ErrorCatalogEntry? Guide => ErrorCatalog.Find(Issue.Code);

    public bool HasEvidence => Issue.Evidence.Count > 0;

    /// <summary>The kept occurrences with their whole exceptions, then what the log said on the way in.</summary>
    public string EvidenceText
    {
        get
        {
            if (Issue.Evidence.Count == 0)
            {
                return string.Empty;
            }
            var text = new StringBuilder();
            foreach (var record in Issue.Evidence)
            {
                text.Append(record.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")).Append(' ')
                    .Append(record.LevelText).Append(' ')
                    .Append(record.Source.Length > 0 ? record.Source : "Optima").Append(": ").AppendLine(record.Message);
                if (record.Exception is { } exception)
                {
                    text.AppendLine(exception.FullText);
                }
            }
            if (Issue.LeadUp.Count > 0)
            {
                text.AppendLine().AppendLine("leading up to the first:");
                foreach (var earlier in Issue.LeadUp)
                {
                    text.Append("  ").Append(earlier.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")).Append(' ')
                        .Append(earlier.LevelText.PadRight(8)).Append(' ')
                        .Append(earlier.ShortSource).Append(": ").AppendLine(earlier.Line);
                }
            }
            return Redactor.Redact(text.ToString().TrimEnd());
        }
    }
}

/// <summary>
/// The Issues tab of DEBUG: what is wrong right now, found by Optima rather than by the player
/// reading the log. Each card says what it is, why, how to fix it, and carries its evidence.
/// </summary>
public sealed partial class IssuesViewModel : ObservableObject
{
    private readonly IssueEngine _engine;
    private readonly ChecksViewModel _checks;
    private readonly ILogger<IssuesViewModel> _logger;

    public IssuesViewModel(IssueEngine engine, ChecksViewModel checks, ILogger<IssuesViewModel> logger)
    {
        _engine = engine;
        _checks = checks;
        _logger = logger;
        // Raised on whatever thread logged; the collections belong to the UI thread.
        _engine.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ObservableCollection<IssueCard> Issues { get; } = [];

    public ObservableCollection<IgnoredIssue> Ignored { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabLabel))]
    private int _attentionCount;

    [ObservableProperty] private string _headline = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasIssues;
    [ObservableProperty] private bool _hasIgnored;

    /// <summary>The tab's chip: the count of what needs attention rides on it.</summary>
    public string TabLabel => AttentionCount > 0 ? $"ISSUES {AttentionCount}" : "ISSUES";

    private void Refresh()
    {
        var current = _engine.Issues;

        // In place, by key: a card that is replaced on every repeat of its error would shut the
        // evidence someone has open under it.
        for (var i = 0; i < current.Count; i++)
        {
            var issue = current[i];
            var at = IndexOf(issue.Key);
            if (at < 0)
            {
                Issues.Insert(i, new IssueCard(issue));
                continue;
            }
            if (at != i)
            {
                Issues.Move(at, i);
            }
            Issues[i].Issue = issue;
        }
        while (Issues.Count > current.Count)
        {
            Issues.RemoveAt(Issues.Count - 1);
        }

        Ignored.Clear();
        foreach (var ignored in _engine.Ignored)
        {
            Ignored.Add(ignored);
        }

        var notes = current.Count(i => !i.NeedsAttention);
        AttentionCount = current.Count - notes;
        HasIssues = current.Count > 0;
        HasIgnored = Ignored.Count > 0;
        Headline = (AttentionCount, notes) switch
        {
            (0, 0) => "All clear. Nothing is wrong that Optima can see.",
            (0, _) => $"Nothing needs attention. {Plural(notes, "note")} below.",
            (_, 0) => $"{Plural(AttentionCount, "issue")} {(AttentionCount == 1 ? "needs" : "need")} attention.",
            _ => $"{Plural(AttentionCount, "issue")} {(AttentionCount == 1 ? "needs" : "need")} attention, and {Plural(notes, "note")}.",
        };
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < Issues.Count; i++)
        {
            if (Issues[i].Key == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Runs every check now; the results reach the list by themselves.</summary>
    [RelayCommand]
    private async Task ScanNowAsync()
    {
        if (IsScanning)
        {
            return;
        }
        IsScanning = true;
        StatusMessage = "running the checks…";
        try
        {
            await _checks.RunAllCommand.ExecuteAsync(null);
            StatusMessage = "checked at " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scanning for issues failed");
            StatusMessage = "the scan failed; its error is on the Log tab";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Dismiss(IssueCard card) => _engine.Dismiss(card.Key);

    [RelayCommand]
    private async Task IgnoreAsync(IssueCard card)
    {
        try
        {
            await _engine.IgnoreAsync(card.Key);
            StatusMessage = "ignored; it is listed under IGNORED and can be brought back";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring an issue failed");
        }
    }

    [RelayCommand]
    private async Task StopIgnoringAsync(IgnoredIssue ignored)
    {
        try
        {
            await _engine.StopIgnoringAsync(ignored.Key);
            StatusMessage = "no longer ignored; it will be raised when it happens again";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring an ignored issue failed");
        }
    }

    /// <summary>The issue as a report that stands on its own, redacted: what to send when reporting it.</summary>
    [RelayCommand]
    private void CopyReport(IssueCard card)
    {
        try
        {
            var environment = new ReportEnvironment(
                typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Environment.OSVersion.VersionString);
            Clipboard.SetText(IssueReport.Format(card.Issue, environment));
            StatusMessage = "report copied";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copying an issue report failed");
            StatusMessage = "could not reach the clipboard";
        }
    }
}
