using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// UPDATES page: launcher self-update (check / install / rollback) and the shipped
/// changelog with the running build's identity. Game news lives on the NEWS page.
/// </summary>
public sealed partial class UpdateLogViewModel : ObservableObject
{
    private readonly LauncherUpdateService _updates;
    private readonly ILogger<UpdateLogViewModel> _logger;
    private LauncherRelease? _available;
    private bool _loaded;

    public UpdateLogViewModel(
        LauncherUpdateService updates,
        ILogger<UpdateLogViewModel> logger)
    {
        _updates = updates;
        _logger = logger;
    }

    public ObservableCollection<ChangelogEntry> Entries { get; } = [];

    [ObservableProperty] private string _buildInfo = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    // ---- Launcher update ----
    [ObservableProperty] private string _launcherStatus = "not checked yet";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private bool _rollbackAvailable;

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
