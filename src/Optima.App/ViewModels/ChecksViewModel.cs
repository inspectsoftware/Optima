using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>
/// The Checks tab of DEBUG (§15): the environment checks, each with a reason and a fix, and the
/// tools for putting things right by hand. HOME's diagnostics widget and the setup wizard read the
/// same results.
/// </summary>
public sealed partial class ChecksViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly RepairService _repair;
    private readonly SettingsService _settings;
    private readonly FirstRunFixService _firstRunFix;
    private readonly StatusViewModel _status;
    private readonly ILogger<ChecksViewModel> _logger;

    public ChecksViewModel(
        IEnumerable<IDiagnosticCheck> checks,
        RepairService repair,
        SettingsService settings,
        FirstRunFixService firstRunFix,
        StatusViewModel status,
        ILogger<ChecksViewModel> logger)
    {
        _checks = checks.OrderBy(c => c.Order).ToList();
        _repair = repair;
        _settings = settings;
        _firstRunFix = firstRunFix;
        _status = status;
        _logger = logger;
    }

    public ObservableCollection<DiagnosticResult> Results { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _heartbeatText = "not checked yet";
    [ObservableProperty] private string _repairStatus = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _ = RefreshHeartbeatAsync();
        if (Results.Count == 0)
        {
            await RunAllAsync(ct);
        }
    }

    [RelayCommand]
    private async Task RunAllAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            return;
        }
        IsRunning = true;
        Results.Clear();
        try
        {
            foreach (var check in _checks)
            {
                DiagnosticResult result;
                try
                {
                    result = await check.RunAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Diagnostic check {Check} crashed", check.Name);
                    result = new DiagnosticResult
                    {
                        CheckName = check.Name,
                        Status = DiagnosticStatus.Fail,
                        Reason = "The check itself failed. Its error is on the Log tab.",
                    };
                }
                Results.Add(result);
            }

            var passed = Results.Count(r => r.Status == DiagnosticStatus.Pass);
            var warned = Results.Count(r => r.Status == DiagnosticStatus.Warning);
            var failed = Results.Count(r => r.Status == DiagnosticStatus.Fail);
            Summary = (failed, warned) switch
            {
                (0, 0) => $"All good. {passed}/{Results.Count} checks passed.",
                (0, _) => $"{passed}/{Results.Count} passed, {warned} warning(s), nothing blocking.",
                (_, 0) => $"{failed} check(s) need attention.",
                _ => $"{failed} check(s) need attention, {warned} warning(s).",
            };
        }
        catch (OperationCanceledException)
        {
            // Leaving the page mid-run is not an error; the next visit runs the checks again.
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private Task RefreshHeartbeatAsync()
        => RunToolAsync(async () => HeartbeatText = await _repair.HeartbeatAsync(), "Reading the platform heartbeat");

    [RelayCommand]
    private Task RestartPlatformAsync() => RunToolAsync(async () =>
    {
        RepairStatus = "restarting Google Play Games...";
        RepairStatus = await _repair.RestartPlatformAsync();
        HeartbeatText = await _repair.HeartbeatAsync();
    }, "Restarting Google Play Games");

    [RelayCommand]
    private Task RedetectAsync() => RunToolAsync(async () =>
    {
        RepairStatus = "re-detecting...";
        RepairStatus = await _repair.RedetectAsync();
    }, "Re-detecting the installation");

    [RelayCommand]
    private void OpenGraphicsSettings()
        => RepairStatus = "graphics settings: " + RepairService.OpenSettingsPage("ms-settings:display-advancedgraphics");

    [RelayCommand]
    private void OpenAppsSettings()
        => RepairStatus = "installed apps: " + RepairService.OpenSettingsPage("ms-settings:appsfeatures");

    [RelayCommand]
    private void RestoreSettingsBackups()
        => RepairStatus = _repair.RestoreSettingsBackups();

    [RelayCommand]
    private void CreateSupportArchive()
        => RepairStatus = _repair.CreateSupportArchive([.. Results]);

    [RelayCommand]
    private void RerunSetup()
    {
        var wizard = new Views.SetupWizardWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        var viewModel = new SetupWizardViewModel(_status, this, _settings, _firstRunFix);
        wizard.DataContext = viewModel;
        _ = viewModel.RunDetectionAsync();
        viewModel.Completed += (_, _) => wizard.Close();
        wizard.ShowDialog();
    }

    /// <summary>
    /// A tool that throws must end as a line on the page. An exception escaping a command reaches
    /// the dispatcher, and the dispatcher's answer to that is to close the app.
    /// </summary>
    private async Task RunToolAsync(Func<Task> tool, string what)
    {
        try
        {
            await tool();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tool} failed", what);
            RepairStatus = what + " failed. Its error is on the Log tab.";
        }
    }
}
