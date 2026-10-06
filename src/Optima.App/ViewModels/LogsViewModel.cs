using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Models;
using Serilog.Events;

namespace Optima.App.ViewModels;

/// <summary>LOGS page (§17): the error guide above the live log.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    public LogsViewModel(LogStreamViewModel stream)
    {
        Stream = stream;
    }

    /// <summary>The live log. The floating console shows this same one.</summary>
    public LogStreamViewModel Stream { get; }

    [ObservableProperty] private string _errorSearchText = string.Empty;

    /// <summary>The error guide, filtered by the guide's own search box.</summary>
    public IReadOnlyList<ErrorCatalogEntry> ErrorEntries
    {
        get
        {
            var search = ErrorSearchText.Trim();
            return search.Length == 0
                ? ErrorCatalog.All
                : ErrorCatalog.All
                    .Where(e => e.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || e.WhatHappened.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
    }

    partial void OnErrorSearchTextChanged(string value) => OnPropertyChanged(nameof(ErrorEntries));

    [RelayCommand]
    private void CopyError(ErrorCatalogEntry entry)
    {
        try
        {
            System.Windows.Clipboard.SetText(ErrorCatalog.FormatPlaintext(entry));
            Stream.StatusMessage = "Copied " + entry.Code + ".";
        }
        catch (Exception)
        {
            Stream.StatusMessage = "Could not reach the clipboard.";
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
