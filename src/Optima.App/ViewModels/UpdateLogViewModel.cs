using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.News;
using Optima.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One news card on the UPDATES page.</summary>
public sealed partial class NewsEntryViewModel : ObservableObject
{
    public NewsEntryViewModel(CopsNewsEntry entry)
    {
        Entry = entry;
    }

    public CopsNewsEntry Entry { get; }

    public string Title => Entry.Version.Length > 0 ? $"{Entry.Name} · {Entry.Version}" : Entry.Name;
    public string StatusTag => "[ " + Entry.Status + " ]";
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
            // The browser refusing to open is not worth a crash.
        }
    }
}

/// <summary>
/// UPDATES page: launcher self-update (check / install / rollback), the official
/// Critical Ops news feed, and the shipped changelog with the running build's identity.
/// </summary>
public sealed partial class UpdateLogViewModel : ObservableObject
{
    private readonly LauncherUpdateService _updates;
    private readonly CopsNewsService _news;
    private readonly ILogger<UpdateLogViewModel> _logger;
    private readonly List<NewsEntryViewModel> _allNews = [];
    private LauncherRelease? _available;
    private bool _loaded;

    public UpdateLogViewModel(
        LauncherUpdateService updates,
        CopsNewsService news,
        ILogger<UpdateLogViewModel> logger)
    {
        _updates = updates;
        _news = news;
        _logger = logger;
    }

    public ObservableCollection<ChangelogEntry> Entries { get; } = [];

    public ObservableCollection<NewsEntryViewModel> News { get; } = [];

    [ObservableProperty] private string _buildInfo = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    // ---- Launcher update ----
    [ObservableProperty] private string _launcherStatus = "not checked yet";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private bool _rollbackAvailable;

    // ---- News ----
    [ObservableProperty] private string _newsFilter = string.Empty;
    [ObservableProperty] private string _newsStatus = "loading feed...";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        var version = typeof(UpdateLogViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var exePath = Environment.ProcessPath;
        var built = exePath is not null && File.Exists(exePath)
            ? File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "unknown";
        BuildInfo = $"version {version} · built {built}";
        RollbackAvailable = _updates.RollbackAvailable;

        LoadChangelog();
        await LoadNewsAsync(ct);
        await CheckNowAsync(ct);
    }

    private void LoadChangelog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            if (!File.Exists(path))
            {
                Status = "CHANGELOG.md was not found next to the executable";
                return;
            }
            foreach (var entry in ChangelogParser.Parse(File.ReadAllText(path)))
            {
                Entries.Add(entry);
            }
            if (Entries.Count == 0)
            {
                Status = "the changelog is empty";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the changelog failed");
            Status = "the changelog could not be read · see logs";
        }
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

    [RelayCommand]
    private async Task CheckNowAsync(CancellationToken ct = default)
    {
        LauncherStatus = "checking...";
        var release = await _updates.CheckAsync(ct);
        if (release is null)
        {
            _available = null;
            UpdateAvailable = false;
            LauncherStatus = "update check unavailable (offline, or no public release yet)";
            return;
        }
        if (LauncherUpdateService.IsNewer(release))
        {
            _available = release;
            UpdateAvailable = true;
            LauncherStatus = $"{release.TagName} is available (published {release.PublishedAt:yyyy-MM-dd})";
        }
        else
        {
            _available = null;
            UpdateAvailable = false;
            LauncherStatus = $"up to date ({release.TagName} is the latest release)";
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (_available is not { } release || UpdateBusy)
        {
            return;
        }
        UpdateBusy = true;
        try
        {
            LauncherStatus = $"downloading {release.TagName}...";
            var staged = await _updates.DownloadAndStageAsync(release);
            LauncherStatus = "restarting to apply the update...";
            await _updates.PrepareApplyAndLaunchSwapAsync(staged);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update install failed");
            LauncherStatus = "update failed: " + ex.Message;
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!_updates.RollbackAvailable || UpdateBusy)
        {
            return;
        }
        UpdateBusy = true;
        try
        {
            LauncherStatus = "restarting into the previous build...";
            await _updates.LaunchRollbackAsync();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed");
            LauncherStatus = "rollback failed: " + ex.Message;
            UpdateBusy = false;
        }
    }
}
