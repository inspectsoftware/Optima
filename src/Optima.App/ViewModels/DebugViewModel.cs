using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Models;

namespace Optima.App.ViewModels;

/// <summary>The tabs of the DEBUG page.</summary>
public enum DebugTab
{
    Checks,
    Log,
    Crashes,
    Guide,
}

/// <summary>
/// DEBUG: everything for finding out what went wrong, in one place. It took over from two rail
/// items, one for environment checks and one for the log, which answered two halves of the same
/// question.
/// </summary>
public sealed partial class DebugViewModel : ObservableObject
{
    public DebugViewModel(ChecksViewModel checks, LogStreamViewModel stream, CrashesViewModel crashes)
    {
        Checks = checks;
        Stream = stream;
        Crashes = crashes;
    }

    public ChecksViewModel Checks { get; }

    /// <summary>The live log. The floating console shows this same one.</summary>
    public LogStreamViewModel Stream { get; }

    public CrashesViewModel Crashes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecks))]
    [NotifyPropertyChangedFor(nameof(IsLog))]
    [NotifyPropertyChangedFor(nameof(IsCrashes))]
    [NotifyPropertyChangedFor(nameof(IsGuide))]
    private DebugTab _selectedTab = DebugTab.Checks;

    public bool IsChecks => SelectedTab == DebugTab.Checks;
    public bool IsLog => SelectedTab == DebugTab.Log;
    public bool IsCrashes => SelectedTab == DebugTab.Crashes;
    public bool IsGuide => SelectedTab == DebugTab.Guide;

    [RelayCommand]
    private void SelectTab(DebugTab tab) => SelectedTab = tab;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Crashes.Load();
        await Checks.InitializeAsync(ct);
    }

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

    [ObservableProperty] private string _guideStatus = string.Empty;

    [RelayCommand]
    private void CopyError(ErrorCatalogEntry entry)
    {
        try
        {
            System.Windows.Clipboard.SetText(ErrorCatalog.FormatPlaintext(entry));
            GuideStatus = "Copied " + entry.Code + ".";
        }
        catch (Exception)
        {
            GuideStatus = "Could not reach the clipboard.";
        }
    }
}
