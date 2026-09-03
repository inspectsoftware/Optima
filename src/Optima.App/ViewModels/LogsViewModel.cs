using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Logging;
using Optima.Core.Configuration;
using Microsoft.Win32;
using Serilog.Events;

namespace Optima.App.ViewModels;

/// <summary>LOGS page (§17): live in-app viewer with level filter and redacted export.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private static readonly string[] Levels = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL"];

    private readonly AppPaths _paths;

    public LogsViewModel(AppPaths paths)
    {
        _paths = paths;
        Entries = App.LogSink.Entries;
        FilteredView = CollectionViewSource.GetDefaultView(Entries);
        FilteredView.Filter = FilterEntry;
        LineCount = Entries.Count;
        Entries.CollectionChanged += (_, _) => LineCount = Entries.Count;
    }

    public ObservableCollection<LogEntry> Entries { get; }
    public ICollectionView FilteredView { get; }
    public IReadOnlyList<string> LevelOptions { get; } = Levels;

    [ObservableProperty] private string _selectedLevel = "TRACE";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _tailEnabled = true;

    [ObservableProperty] private int _lineCount;

    partial void OnSelectedLevelChanged(string value) => FilteredView.Refresh();
    partial void OnSearchTextChanged(string value) => FilteredView.Refresh();

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
        return SearchText.Length == 0
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private async Task ExportAsync()
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

        // Tokens or credentials never belong in an exported log (§17).
        var lines = Entries.Select(e => LogRedactor.Redact($"{e.TimeText} [{e.Level,-8}] {e.Source}: {e.Message}"));
        await File.WriteAllLinesAsync(dialog.FileName, lines);
        StatusMessage = $"Exported {Entries.Count} entries.";
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

    public static LogEventLevel ToSerilogLevel(string name) => name switch
    {
        "Trace" or "TRACE" => LogEventLevel.Verbose,
        "Debug" or "DEBUG" => LogEventLevel.Debug,
        "Warning" or "WARN" => LogEventLevel.Warning,
        "Error" or "ERROR" => LogEventLevel.Error,
        "Critical" or "CRITICAL" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
