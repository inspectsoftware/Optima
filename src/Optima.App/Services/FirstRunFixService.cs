using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Ipc;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>What the readiness analysis found, and what a fix run did about it.</summary>
public sealed record ReadinessReport(
    bool FirmwareVirtualizationOff,
    bool HypervisorFeaturesMissing,
    bool GpgMissing)
{
    public bool AnythingAutomatable => HypervisorFeaturesMissing || GpgMissing;
    public bool AllGood => !FirmwareVirtualizationOff && !HypervisorFeaturesMissing && !GpgMissing;
}

public sealed record FixRunResult(IReadOnlyList<string> Log, bool RestartRequired);

/// <summary>
/// The "fix everything" engine behind the first-run wizard (Q7): one consent, then every automatable fix runs through
/// the existing elevation broker.
/// </summary>
public sealed class FirstRunFixService
{
    private const string GpgDownloadPage = "https://play.google.com/googleplaygames";

    private readonly IElevationBroker _broker;
    private readonly ISystemInfoService _systemInfo;
    private readonly IGameDetector _detector;
    private readonly ILogger<FirstRunFixService> _logger;

    public FirstRunFixService(
        IElevationBroker broker,
        ISystemInfoService systemInfo,
        IGameDetector detector,
        ILogger<FirstRunFixService> logger)
    {
        _broker = broker;
        _systemInfo = systemInfo;
        _detector = detector;
        _logger = logger;
    }

    public async Task<ReadinessReport> AnalyzeAsync(CancellationToken ct = default)
    {
        var virtualization = await _systemInfo.GetVirtualizationStateAsync(ct);
        var platform = await _detector.DetectPlatformAsync(ct);

        var hypervisorUsable = virtualization.HypervisorPresent == true
                               || virtualization.HyperVFeatureEnabled == true
                               || virtualization.VirtualMachinePlatformEnabled == true
                               || virtualization.WindowsHypervisorPlatformEnabled == true;

        return new ReadinessReport(
            FirmwareVirtualizationOff: virtualization.FirmwareVirtualizationEnabled == false,
            HypervisorFeaturesMissing: !hypervisorUsable && virtualization.FirmwareVirtualizationEnabled != false,
            GpgMissing: platform is null);
    }

    public async Task<FixRunResult> RunFixesAsync(ReadinessReport report, CancellationToken ct = default)
    {
        var log = new List<string>();
        var restartRequired = false;

        if (report.HypervisorFeaturesMissing)
        {
            if (!await _broker.EnsureStartedAsync(ct))
            {
                log.Add("Hypervisor features: skipped, the administrator prompt was declined.");
            }
            else
            {
                foreach (var feature in new[] { "HypervisorPlatform", "VirtualMachinePlatform" })
                {
                    var response = await _broker.SendAsync(new IpcRequest
                    {
                        Command = IpcCommand.EnableWindowsFeature,
                        Args = { ["feature"] = feature },
                    }, ct);
                    if (response.Success)
                    {
                        var needsRestart = response.Data.GetValueOrDefault("restartRequired") == "1";
                        restartRequired |= needsRestart;
                        log.Add($"{feature}: enabled{(needsRestart ? " (restart required)" : "")}.");
                    }
                    else
                    {
                        log.Add($"{feature}: failed - {response.Error}");
                    }
                    _logger.LogInformation("EnableWindowsFeature {Feature}: {Ok} {Error}",
                        feature, response.Success, response.Error);
                }
                _systemInfo.InvalidateCache();
            }
        }

        if (report.GpgMissing)
        {
            try
            {
                Process.Start(new ProcessStartInfo(GpgDownloadPage) { UseShellExecute = true });
                log.Add("Google Play Games: the official download page was opened in your browser; run the installer from there.");
            }
            catch (Exception ex)
            {
                log.Add("Google Play Games: the download page could not be opened - " + ex.Message);
            }
        }

        return new FixRunResult(log, restartRequired);
    }

    public string? ScheduleRestart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                using var runOnce = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
                runOnce.SetValue("OptimaSetupResume", $"\"{exe}\"");
            }
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"Optima setup: restarting to finish enabling virtualization features\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void ClearResume()
    {
        try
        {
            using var runOnce = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
            runOnce?.DeleteValue("OptimaSetupResume", throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
