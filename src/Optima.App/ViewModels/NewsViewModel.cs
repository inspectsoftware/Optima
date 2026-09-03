using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.News;

namespace Optima.App.ViewModels;

/// <summary>One news card on the NEWS page.</summary>
public sealed partial class NewsEntryViewModel : ObservableObject
{
    public NewsEntryViewModel(CopsNewsEntry entry)
    {
        Entry = entry;
    }

    public CopsNewsEntry Entry { get; }

    public string Title => Entry.Version.Length > 0 ? $"{Entry.Name} · {Entry.Version}" : Entry.Name;
    public string StatusTag => Entry.Status.ToUpperInvariant();
    public bool IsLive => Entry.IsLive;
    public IReadOnlyList<string> Headlines => Entry.Headlines;

    public bool Matches(string filter)
        => filter.Length == 0
           || Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || Entry.Headlines.Any(h => h.Contains(filter, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void OpenNotes()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Entry.NotesUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}

/// <summary>NEWS page: the official Critical Ops updates feed, filterable.</summary>
public sealed partial class NewsViewModel : ObservableObject
{
    private readonly CopsNewsService _news;
    private readonly List<NewsEntryViewModel> _allNews = [];
    private bool _loaded;

    public NewsViewModel(CopsNewsService news)
    {
        _news = news;
    }

    public ObservableCollection<NewsEntryViewModel> News { get; } = [];

    [ObservableProperty] private string _newsFilter = string.Empty;
    [ObservableProperty] private string _newsStatus = "loading feed...";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await LoadNewsAsync(ct);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        NewsStatus = "loading feed...";
        await LoadNewsAsync(ct);
    }

    private async Task LoadNewsAsync(CancellationToken ct)
    {
        var entries = await _news.GetEntriesAsync(ct);
        _allNews.Clear();
        _allNews.AddRange(entries.Select(e => new NewsEntryViewModel(e)));
        ApplyNewsFilter();
        NewsStatus = entries.Count == 0
            ? "the official updates feed is unreachable right now (and nothing is cached)"
            : string.Empty;
    }

    partial void OnNewsFilterChanged(string value) => ApplyNewsFilter();

    private void ApplyNewsFilter()
    {
        News.Clear();
        var filter = NewsFilter.Trim();
        foreach (var entry in _allNews.Where(e => e.Matches(filter)))
        {
            News.Add(entry);
        }
    }
}
