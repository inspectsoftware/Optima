using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optima.App.Services;
using Optima.Core.Configuration;
using Optima.Core.Crashes;
using Microsoft.Extensions.Logging;

namespace Optima.App.ViewModels;

/// <summary>One captured crash bundle folder in the CRASHES list.</summary>
public sealed record CrashBundleRow(string Name, string CapturedText, string Path);

/// <summary>The Crashes tab of DEBUG (§15): the bundles the Watchdog saves when the game ends badly.</summary>
public sealed partial class CrashesViewModel : ObservableObject
{
    private readonly CrashSentinel _crashSentinel;
    private readonly AppPaths _paths;
    private readonly ILogger<CrashesViewModel> _logger;

    public CrashesViewModel(CrashSentinel crashSentinel, AppPaths paths, ILogger<CrashesViewModel> logger)
    {
        _crashSentinel = crashSentinel;
        _paths = paths;
        _logger = logger;
        _crashSentinel.BundleWritten += _ =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Load);
    }

    public ObservableCollection<CrashBundleRow> Crashes { get; } = [];

    [ObservableProperty] private string _crashStatus = string.Empty;

    public void Load()
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
        try
        {
            var folder = await _crashSentinel.CaptureManualAsync();
            CrashStatus = folder is null
                ? "capture failed, its error is on the Log tab"
                : "captured " + System.IO.Path.GetFileName(folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual crash capture failed");
            CrashStatus = "capture failed, its error is on the Log tab";
        }
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
            CrashStatus = "export failed, its error is on the Log tab";
        }
    }
}
