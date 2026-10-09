using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.Core.Configuration;
using Optima.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// The update notice on HOME and the same thing on the UPDATES page: one state, shown twice.
/// A start asks once whether a newer Optima is out. Nothing is downloaded until Update is pressed,
/// and nothing that was downloaded is run unless it carries Optima's signature.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    // What the setup is told: no pages, no questions, no restart of Windows, and start Optima
    // again when done (installer.iss reads /relaunch).
    private const string SetupArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1";

    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly Func<bool> _gameRunning;
    private readonly ILogger<UpdateViewModel> _logger;
    private UpdateRelease? _release;
    private bool _dismissed;

    public UpdateViewModel(
        UpdateService updates, SettingsService settings, Func<bool> gameRunning, ILogger<UpdateViewModel> logger)
    {
        _updates = updates;
        _settings = settings;
        _gameRunning = gameRunning;
        _logger = logger;
    }

    /// <summary>Closes Optima so the setup can replace it. Set by the app once it has a way out.</summary>
    public Action? ExitForUpdate { get; set; }

    /// <summary>True from the moment a newer version is known until it is installed or put off for this run.</summary>
    [ObservableProperty] private bool _bannerVisible;

    /// <summary>"Optima 0.8.1 is available", then the download's progress.</summary>
    [ObservableProperty] private string _bannerText = string.Empty;

    /// <summary>Why the last attempt did not go through. Empty when there is nothing to say.</summary>
    [ObservableProperty] private string _problem = string.Empty;

    /// <summary>One line for the UPDATES page: what the last check found.</summary>
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _updateAvailable;

    /// <summary>The check a start makes by itself, when Settings allows it. Never throws.</summary>
    public async Task CheckAtStartAsync()
    {
        try
        {
            if ((await _settings.GetSettingsAsync()).CheckForUpdatesAtStart)
            {
                await CheckAsync();
            }
        }
        catch (Exception ex)
        {
            // The notice is a courtesy; a failed check must never disturb a start.
            _logger.LogDebug(ex, "The update check at start failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNowAsync()
    {
        // Asked for by hand: a notice that was put off comes back.
        _dismissed = false;
        Status = "asking GitHub...";
        await CheckAsync();
    }

    private bool CanCheck() => !IsBusy;

    private async Task CheckAsync()
    {
        var result = await _updates.CheckAsync();
        Status = result.Detail;
        if (result.Status != UpdateCheckStatus.Available || result.Release is null)
        {
            return;
        }

        _release = result.Release;
        UpdateAvailable = true;
        Problem = string.Empty;
        BannerText = $"Optima {_release.VersionText} is available. You have {UpdateSignature.Three(UpdateService.CurrentVersion)}.";
        BannerVisible = !_dismissed;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_release is not { } release)
        {
            return;
        }
        // The update ends with Optima closing, and closing Optima ends a session.
        if (_gameRunning())
        {
            Problem = "Close Critical Ops first. Optima restarts to update, and that would end your session.";
            return;
        }

        IsBusy = true;
        Problem = string.Empty;
        var available = BannerText;
        try
        {
            BannerText = $"Downloading Optima {release.VersionText}...";
            var progress = new Progress<double>(done => BannerText = $"Downloading Optima {release.VersionText}... {done:P0}");
            var download = await _updates.DownloadAsync(release, progress);
            if (download.SetupPath is not { } setup)
            {
                Problem = download.Detail;
                BannerText = available;
                return;
            }

            BannerText = "Waiting for Windows: the setup asks for administrator approval once.";
            try
            {
                // On the pool: this only returns once the prompt has been answered.
                await Task.Run(() =>
                {
                    using var process = Process.Start(new ProcessStartInfo(setup, SetupArguments) { UseShellExecute = true });
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Problem = "Windows asked for administrator approval and the answer was No. Optima was not updated.";
                BannerText = available;
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _logger.LogWarning(ex, "The update setup could not be started");
                Problem = "The setup could not be started: " + ex.Message;
                BannerText = available;
                return;
            }

            _logger.LogInformation("Update {Version} started; closing for it", release.VersionText);
            BannerText = $"Installing Optima {release.VersionText}. Optima closes now and opens again when it is done.";
            ExitForUpdate?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => UpdateAvailable && !IsBusy;

    /// <summary>Later. The notice is back at the next start.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = true;
        BannerVisible = false;
    }

    [RelayCommand]
    private void OpenReleases()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Problem = "The releases page could not be opened: " + UpdateService.ReleasesPage;
        }
    }
}
