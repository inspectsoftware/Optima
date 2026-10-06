using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>A repair as a button on an issue's card.</summary>
public sealed record RepairButton(string IssueKey, string RepairId, string Title, string Changes, RepairTier Tier)
{
    /// <summary>What pressing it costs, said before it is pressed.</summary>
    public string ToolTip => Tier switch
    {
        RepairTier.Elevated => Changes + " Needs administrator rights.",
        RepairTier.Disruptive => Changes + " Interrupts what is running.",
        _ => Changes,
    };
}

/// <summary>One repair that ran, as a row of the history.</summary>
public sealed record RepairHistoryRow(string When, string Who, string What, string About, string Outcome, string Summary, bool Worked);

/// <summary>One issue as a card. The card outlives a refresh, so an open evidence expander stays open while the count climbs.</summary>
public sealed partial class IssueCard : ObservableObject
{
    public IssueCard(Issue issue, IReadOnlyList<RepairButton>? repairs = null)
    {
        _issue = issue;
        Repairs = repairs ?? [];
    }

    /// <summary>What can be done about it from here. Empty for the many issues only the player can fix.</summary>
    public IReadOnlyList<RepairButton> Repairs { get; }

    public bool HasRepairs => Repairs.Count > 0;

    /// <summary>What was last done about it, or what it is waiting for.</summary>
    public string RepairNote => Issue.RepairNote;

    public bool IsRepaired => Issue.State == IssueState.Repaired;

    public bool IsRepairing => Issue.State == IssueState.Repairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairNote))]
    [NotifyPropertyChangedFor(nameof(IsRepaired))]
    [NotifyPropertyChangedFor(nameof(IsRepairing))]
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

    public string SeverityTag => IsRepaired ? "REPAIRED" : Issue.Severity switch
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
    private static readonly string[] Modes = ["off", "safe repairs only", "everything it can"];

    private readonly IssueEngine _engine;
    private readonly RepairRunner _repairs;
    private readonly ChecksViewModel _checks;
    private readonly SettingsService _settings;
    private readonly ILogger<IssuesViewModel> _logger;

    public IssuesViewModel(
        IssueEngine engine, RepairRunner repairs, ChecksViewModel checks, SettingsService settings, ILogger<IssuesViewModel> logger)
    {
        _engine = engine;
        _repairs = repairs;
        _checks = checks;
        _settings = settings;
        _logger = logger;
        _selectedMode = Modes[(int)(settings.Current?.AutoRepair ?? AutoRepairMode.Escalate)];
        // Raised on whatever thread logged; the collections belong to the UI thread.
        _engine.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ObservableCollection<IssueCard> Issues { get; } = [];

    public ObservableCollection<IgnoredIssue> Ignored { get; } = [];

    /// <summary>The latest repairs, newest first: what was run, by whom, and how it went.</summary>
    public ObservableCollection<RepairHistoryRow> History { get; } = [];

    [ObservableProperty] private bool _hasHistory;

    /// <summary>How many repairs the history shows. The file keeps more; the page shows the recent ones.</summary>
    private const int HistoryRows = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabLabel))]
    private int _attentionCount;

    [ObservableProperty] private string _headline = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasIssues;
    [ObservableProperty] private bool _hasIgnored;

    public IReadOnlyList<string> ModeOptions { get; } = Modes;

    /// <summary>How far Optima goes on its own; saved the moment it is changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    private string _selectedMode;

    public string ModeText => Array.IndexOf(Modes, SelectedMode) switch
    {
        0 => "Optima lists what it finds and repairs nothing until you press a button.",
        2 => "Safe repairs first. Where they do not help, Optima goes on by itself to the ones that interrupt or need administrator rights. Never while a game is running, and never a prompt over a hidden window.",
        _ => "Repairs that are reversible, need no administrator rights and interrupt nothing run by themselves. Anything more waits for you.",
    };

    partial void OnSelectedModeChanged(string value) => _ = SaveModeAsync((AutoRepairMode)Math.Max(0, Array.IndexOf(Modes, value)));

    private async Task SaveModeAsync(AutoRepairMode mode)
    {
        try
        {
            if (_settings.Current?.AutoRepair != mode)
            {
                // The runner listens for settings changes and looks again by itself.
                await _settings.UpdateSettingsAsync(s => s with { AutoRepair = mode });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the automatic repair mode failed");
        }
    }

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
                Issues.Insert(i, new IssueCard(issue, _repairs.ActionsFor(issue)
                    .Select(a => new RepairButton(issue.Key, a.Id, a.Title, a.Changes, a.Tier))
                    .ToList()));
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

        History.Clear();
        foreach (var attempt in _engine.Attempts.Reverse().Take(HistoryRows))
        {
            History.Add(new RepairHistoryRow(
                attempt.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                attempt.Trigger switch
                {
                    RepairTrigger.User => "you",
                    RepairTrigger.Launch => "Optima, in a launch",
                    _ => "Optima",
                },
                attempt.RepairId == LaunchSupport.ReloadDriverRepair ? "reload the display driver" : _repairs.TitleFor(attempt.RepairId),
                Services.RepairNotice.About(attempt),
                attempt.Outcome switch
                {
                    RepairOutcome.Fixed => "done",
                    RepairOutcome.NotNeeded => "nothing needed",
                    RepairOutcome.NeedsUser => "left to you",
                    _ => "did not work",
                },
                Redactor.Redact(attempt.Summary),
                attempt.Outcome is RepairOutcome.Fixed or RepairOutcome.NotNeeded));
        }
        HasHistory = History.Count > 0;

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

    /// <summary>Runs a repair because the player pressed its button. The click is the permission.</summary>
    [RelayCommand]
    private async Task RunRepairAsync(RepairButton repair)
    {
        try
        {
            StatusMessage = repair.Title + "…";
            // Off the UI thread: a repair counts processes, waits on the helper, writes files.
            var result = await Task.Run(() => _repairs.RunAsync(repair.IssueKey, repair.RepairId));
            StatusMessage = result.Outcome switch
            {
                RepairOutcome.Fixed => "done: " + repair.Title,
                RepairOutcome.NotNeeded => "nothing needed doing",
                RepairOutcome.NeedsUser => "the rest is yours; see the card",
                _ => "it did not work; see the card",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Repair {Repair} could not be run", repair.RepairId);
            StatusMessage = "the repair could not be run; its error is on the Log tab";
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
