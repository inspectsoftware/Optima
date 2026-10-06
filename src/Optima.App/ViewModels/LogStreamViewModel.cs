using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Optima.App.Logging;
using Optima.Core.Configuration;
using Optima.Core.Health;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>
/// The live log: the list, its filters, and the detail of one selected line (§17). Shared by the
/// LOGS page and the floating console, which is why it is a view model of its own.
/// </summary>
public sealed partial class LogStreamViewModel : ObservableObject
{
    private static readonly string[] Levels = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL"];

    /// <summary>How many earlier lines a copied report takes along as context.</summary>
    private const int LeadUpLines = 15;

    private readonly AppPaths _paths;
    private readonly ILogger<LogStreamViewModel> _logger;
    private readonly ListCollectionView _view;

    public LogStreamViewModel(AppPaths paths, ILogger<LogStreamViewModel> logger)
    {
        _paths = paths;
        _logger = logger;
        Entries = App.LogSink.Entries;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
        _view.Filter = FilterEntry;
        Count();
        Entries.CollectionChanged += (_, _) => Count();
    }

    public ObservableCollection<LogEntry> Entries { get; }

    public ICollectionView FilteredView => _view;

    public IReadOnlyList<string> LevelOptions { get; } = Levels;

    [ObservableProperty] private string _selectedLevel = "TRACE";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _tailEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _lineCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _shownCount;

    public string CountText => ShownCount == LineCount
        ? $"{LineCount} lines"
        : $"{ShownCount} of {LineCount} lines";

    /// <summary>
    /// The line whose detail is open. Set by a click on a row and cleared by "close", never by the
    /// list itself: a trim resets the list's own selection, and that must not shut a detail that
    /// someone is reading.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedHeader))]
    [NotifyPropertyChangedFor(nameof(SelectedMessage))]
    [NotifyPropertyChangedFor(nameof(SelectedNativeText))]
    [NotifyPropertyChangedFor(nameof(SelectedExceptionText))]
    [NotifyPropertyChangedFor(nameof(SelectedArgumentsText))]
    [NotifyPropertyChangedFor(nameof(SelectedGuide))]
    private LogEntry? _selected;

    public bool HasSelection => Selected is not null;

    public string SelectedHeader => Selected is { } entry
        ? $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  ·  {(entry.Record.Source.Length > 0 ? entry.Record.Source : "Optima")}"
        : string.Empty;

    /// <summary>
    /// The detail is redacted even on screen: a message or a stack trace is full of profile paths,
    /// and a screenshot of this pane is the most likely way it leaves the PC.
    /// </summary>
    public string SelectedMessage => Selected is { } entry ? Redactor.Redact(entry.Record.Message) : string.Empty;

    /// <summary>What Windows itself said, when the line is a failed Windows call.</summary>
    public string SelectedNativeText => Selected?.Record.Exception is { NativeErrorCode: { } code } exception
        ? $"Windows error {code}: {exception.NativeErrorText}"
        : string.Empty;

    public string SelectedExceptionText => Selected?.Record.Exception is { } exception
        ? Redactor.Redact(exception.FullText)
        : string.Empty;

    public string SelectedArgumentsText => Selected is { Record.Properties.Count: > 0 } entry
        ? Redactor.Redact(string.Join(Environment.NewLine, entry.Record.Properties
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} = {p.Value}")))
        : string.Empty;

    /// <summary>The error guide's entry for the line's code, so the fix is next to the failure.</summary>
    public ErrorCatalogEntry? SelectedGuide => Selected is { } entry ? ErrorCatalog.Find(entry.Record.ErrorCode) : null;

    partial void OnSelectedLevelChanged(string value) => Refilter();
    partial void OnSearchTextChanged(string value) => Refilter();

    private void Refilter()
    {
        _view.Refresh();
        Count();
    }

    private void Count()
    {
        LineCount = Entries.Count;
        ShownCount = _view.Count;
    }

    private bool FilterEntry(object item)
    {
        if (item is not LogEntry entry)
        {
            return false;
        }
        var minIndex = Array.IndexOf(Levels, SelectedLevel);
        var entryIndex = Array.IndexOf(Levels, entry.Level);
        if (entryIndex >= 0 && minIndex >= 0 && entryIndex < minIndex)
        {
            return false;
        }
        // The exception is searched too: a stack frame or a Windows error code is often the only
        // thing known about the line being looked for.
        return SearchText.Length == 0
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Record.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Record.Exception?.FullText.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true;
    }

    [RelayCommand]
    private void Clear()
    {
        Selected = null;
        Entries.Clear();
    }

    [RelayCommand]
    private void CloseDetail() => Selected = null;

    /// <summary>
    /// Copies the selected line as a report that stands on its own: build, full error, the guide's
    /// entry for it, and the lines that led up to it. Redacted like every export.
    /// </summary>
    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is not { } entry)
        {
            return;
        }
        try
        {
            var index = Entries.IndexOf(entry);
            var first = Math.Max(0, index - LeadUpLines);
            var leadUp = index <= 0
                ? []
                : Entries.Skip(first).Take(index - first).Select(e => e.Record).ToList();
            var environment = new ReportEnvironment(
                typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Environment.OSVersion.VersionString);
            System.Windows.Clipboard.SetText(LogReport.FormatReport(entry.Record, leadUp, environment));
            StatusMessage = "Report copied, with the " + leadUp.Count + " lines before it.";
        }
        catch (Exception ex)
        {
            // The clipboard belongs to whichever program holds it open; a command must not take the app down.
            _logger.LogDebug(ex, "Copying a log report failed");
            StatusMessage = "Could not reach the clipboard.";
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt",
                FileName = $"optima-export-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log",
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            // Dates, full source names and whole exceptions: an export is read by someone who was
            // not there. Tokens, the user name and profile paths never belong in one (§17).
            var records = Entries.Select(e => e.Record).ToList();
            await File.WriteAllTextAsync(dialog.FileName, LogReport.FormatExport(records));
            StatusMessage = $"Exported {records.Count} lines.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Could not write the export: " + ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log export failed");
            StatusMessage = "The export failed.";
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.LogsDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            StatusMessage = "Could not open the log folder.";
        }
    }
}
