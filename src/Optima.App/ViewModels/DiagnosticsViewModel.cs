using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Crashes;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One captured crash bundle folder in the CRASHES list.</summary>
public sealed record CrashBundleRow(string Name, string CapturedText, string Path);

/// <summary>DIAGNOSTICS page (§15): environment checks plus the Watchdog's crash bundles.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly CrashSentinel _crashSentinel;
    private readonly AppPaths _paths;
    private readonly ILogger<DiagnosticsViewModel> _logger;

    public DiagnosticsViewModel(
        IEnumerable<IDiagnosticCheck> checks,
        CrashSentinel crashSentinel,
        AppPaths paths,
        ILogger<DiagnosticsViewModel> logger)
    {
        _checks = checks.OrderBy(c => c.Order).ToList();
        _crashSentinel = crashSentinel;
        _paths = paths;
        _logger = logger;
        _crashSentinel.BundleWritten += _ =>
            System.Windows.Application.Current?.Dispatcher.Invoke(LoadCrashes);
    }

    public ObservableCollection<DiagnosticResult> Results { get; } = [];

    public ObservableCollection<CrashBundleRow> Crashes { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _crashStatus = string.Empty;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        LoadCrashes();
        if (Results.Count == 0)
        {
            await RunAllAsync(ct);
        }
    }

    private void LoadCrashes()
    {
        Crashes.Clear();
        try
        {
            if (!Directory.Exists(_paths.CrashesDirectory))
            {
                return;
            }
            foreach (var dir in Directory.EnumerateDirectories(_paths.CrashesDirectory)
                         .OrderByDescending(d => d, StringComparer.Ordinal)
                         .Take(20))
            {
                var name = System.IO.Path.GetFileName(dir);
                var captured = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm");
                Crashes.Add(new CrashBundleRow(name, captured, dir));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Crash bundle listing failed");
        }
    }

    [RelayCommand]
    private async Task CaptureCrashNowAsync()
    {
        CrashStatus = "capturing...";
        var folder = await _crashSentinel.CaptureManualAsync();
        CrashStatus = folder is null ? "capture failed, see Logs" : "captured " + System.IO.Path.GetFileName(folder);
    }

    [RelayCommand]
    private void OpenCrashesFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.CrashesDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.CrashesDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening the crashes folder failed");
        }
    }

    [RelayCommand]
    private void ExportCrashRedacted(CrashBundleRow row)
    {
        try
        {
            var zip = CrashExporter.ExportRedactedZip(row.Path);
            CrashStatus = "redacted zip ready: " + System.IO.Path.GetFileName(zip);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redacted export failed for {Bundle}", row.Name);
            CrashStatus = "export failed, see Logs";
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
                        Reason = "The check itself failed. See Logs.",
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
        finally
        {
            IsRunning = false;
        }
    }
}
