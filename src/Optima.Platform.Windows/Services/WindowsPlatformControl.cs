using System.ComponentModel;
using System.Diagnostics;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Detection;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>Google Play Games' processes, found by name and by the folder they run from.</summary>
public sealed class WindowsPlatformControl : IPlatformControl
{
    private readonly IGameDetector _detector;
    private readonly SettingsService _settings;
    private readonly ILogger<WindowsPlatformControl> _logger;

    public WindowsPlatformControl(IGameDetector detector, SettingsService settings, ILogger<WindowsPlatformControl> logger)
    {
        _detector = detector;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int> CountRunningAsync(CancellationToken ct = default)
    {
        var rules = await _settings.GetDetectionRulesAsync(ct).ConfigureAwait(false);
        var roots = await PlatformRootsAsync(rules, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (IsPlatformProcess(process, rules, roots))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
        if (platform is null || platform.BootstrapperPath.Length == 0)
        {
            return false;
        }
        using var started = Process.Start(new ProcessStartInfo(platform.BootstrapperPath) { UseShellExecute = true });
        return true;
    }

    public async Task<int> StopAsync(CancellationToken ct = default)
    {
        var rules = await _settings.GetDetectionRulesAsync(ct).ConfigureAwait(false);
        var roots = await PlatformRootsAsync(rules, ct).ConfigureAwait(false);
        var killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsPlatformProcess(process, rules, roots))
                {
                    continue;
                }
                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
            {
                _logger.LogDebug(ex, "Could not kill a platform process");
            }
            finally
            {
                process.Dispose();
            }
        }
        return killed;
    }

    /// <summary>The folders Google Play Games can run from: the known ones, the manual one, the detected one.</summary>
    private async Task<IReadOnlyList<string>> PlatformRootsAsync(DetectionRules rules, CancellationToken ct)
    {
        var roots = rules.KnownInstallFolders.Select(Environment.ExpandEnvironmentVariables).ToList();
        if (!string.IsNullOrWhiteSpace(rules.ManualInstallPath))
        {
            roots.Add(Environment.ExpandEnvironmentVariables(rules.ManualInstallPath));
        }
        var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
        if (platform is { BootstrapperPath.Length: > 0 } && Path.GetDirectoryName(platform.BootstrapperPath) is { Length: > 0 } detected)
        {
            roots.Add(detected);
        }
        return roots;
    }

    /// <summary>
    /// The patterns include names as common as "client" and "Service", so a name match alone is not
    /// enough: the executable also has to live under a Google Play Games folder. A process whose
    /// path cannot be read is left alone.
    /// </summary>
    private static bool IsPlatformProcess(Process process, DetectionRules rules, IReadOnlyList<string> roots)
    {
        try
        {
            if (!GameDetectionEngine.MatchesAny(process.ProcessName, rules.PlatformProcessPatterns)
                && !GameDetectionEngine.MatchesAny(process.ProcessName, rules.EmulatorProcessPatterns))
            {
                return false;
            }
            var path = process.MainModule?.FileName;
            return path is not null && roots.Any(root =>
                path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
