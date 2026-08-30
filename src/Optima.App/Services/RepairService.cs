using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Crashes;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.App.Services;

/// <summary>
/// The repair actions behind the DIAGNOSTICS page: platform heartbeat, clean restart,
/// re-detection, settings restore from the rolling backups, and the redacted support
/// archive. Everything reports outcomes as plain sentences for the status line.
/// </summary>
public sealed class RepairService
{
    private readonly IGameDetector _detector;
    private readonly SettingsService _settings;
    private readonly GpgLogReader _gpgLogs;
    private readonly AppPaths _paths;
    private readonly ILogger<RepairService> _logger;

    public RepairService(
        IGameDetector detector,
        SettingsService settings,
        GpgLogReader gpgLogs,
        AppPaths paths,
        ILogger<RepairService> logger)
    {
        _detector = detector;
        _settings = settings;
        _gpgLogs = gpgLogs;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>One-line health verdict for Google Play Games on this machine.</summary>
    public async Task<string> HeartbeatAsync(CancellationToken ct = default)
    {
        var rules = await _settings.GetDetectionRulesAsync(ct);
        var alive = CountPlatformProcesses(rules);
        var logAge = _gpgLogs.ServiceLogAgeMinutes();
        var logText = logAge is { } age
            ? age < 10 ? $"service log written {age:F0} min ago" : $"service log stale ({age:F0} min)"
            : "no service log found";
        return alive == 0
            ? $"Google Play Games is not running · {logText}"
            : $"{alive} platform process(es) running · {logText}";
    }

    /// <summary>
    /// Restarts the platform cleanly: game-facing processes first, then services, then the
    /// Bootstrapper is launched fresh. Kills only the platform's own processes, by the same
    /// name patterns detection uses.
    /// </summary>
    public async Task<string> RestartPlatformAsync(CancellationToken ct = default)
    {
        var rules = await _settings.GetDetectionRulesAsync(ct);
        var killed = 0;

        // Emulator first (the game dies with it), then the client/service processes.
        foreach (var pattern in rules.EmulatorProcessPatterns.Concat(rules.PlatformProcessPatterns))
        {
            var name = pattern.Trim('^', '$');
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(ex, "Could not kill {Name}", name);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        await Task.Delay(1500, ct);

        var platform = await _detector.DetectPlatformAsync(ct);
        if (platform is null || platform.BootstrapperPath.Length == 0)
        {
            return $"stopped {killed} process(es); Google Play Games was not found to relaunch";
        }
        try
        {
            Process.Start(new ProcessStartInfo(platform.BootstrapperPath) { UseShellExecute = true });
            return $"stopped {killed} process(es) and relaunched Google Play Games";
        }
        catch (Exception ex)
        {
            return $"stopped {killed} process(es); relaunch failed: {ex.Message}";
        }
    }

    /// <summary>Clears the cached fast-start paths and re-runs detection from scratch.</summary>
    public async Task<string> RedetectAsync(CancellationToken ct = default)
    {
        await _settings.UpdateSettingsAsync(s => s with
        {
            CachedGpgInstallDirectory = null,
            CachedGameLaunchUri = null,
        }, ct);
        var platform = await _detector.DetectPlatformAsync(ct);
        var game = await _detector.DetectTargetGameAsync(ct);
        return platform is null
            ? "Google Play Games was not found; if it moved, set the folder under Settings > path overrides"
            : $"found Google Play Games {platform.Version} · Critical Ops {(game is null ? "not installed" : "installed")}";
    }

    public static string OpenSettingsPage(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return "opened";
        }
        catch (Exception ex)
        {
            return "could not open: " + ex.Message;
        }
    }

    /// <summary>Restores config/detection/profiles from the .bak generation JsonStore keeps.</summary>
    public string RestoreSettingsBackups()
    {
        var restored = 0;
        foreach (var file in new[] { _paths.ConfigFile, _paths.DetectionFile, _paths.ProfilesFile })
        {
            var backup = file + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    File.Copy(backup, file, overwrite: true);
                    restored++;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Restoring {File} failed", file);
                }
            }
        }
        return restored == 0
            ? "no backups exist yet (they appear after the next settings save)"
            : $"restored {restored} file(s) from backup; restart Optima to load them";
    }

    /// <summary>
    /// Builds the redacted support archive: recent Optima logs, the diagnostics results,
    /// the newest crash bundle, and the settings files, every text entry scrubbed of
    /// secrets, user names and machine names first.
    /// </summary>
    public string CreateSupportArchive(IReadOnlyList<DiagnosticResult> diagnostics)
    {
        try
        {
            var supportDir = Path.Combine(_paths.Root, "support");
            Directory.CreateDirectory(supportDir);
            var zipPath = Path.Combine(supportDir, $"optima-support-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                void AddText(string entryName, string content)
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(CrashExporter.RedactText(content));
                }

                AddText("diagnostics.txt", string.Join(Environment.NewLine, diagnostics.Select(d =>
                    $"[{d.Status}] {d.CheckName}: {d.Reason}{(d.RecommendedFix.Length > 0 ? " | fix: " + d.RecommendedFix : "")}")));

                AddText("system.txt",
                    $"optima {typeof(RepairService).Assembly.GetName().Version?.ToString(3)}\r\n" +
                    $"windows {Environment.OSVersion.VersionString} 64bit={Environment.Is64BitOperatingSystem}\r\n" +
                    $"created {DateTimeOffset.Now:O}");

                // The app's own logs are optima-YYYYMMDD.log; the admin arm writes
                // optima-watchdog-*.log. Both matter for support, newest first by write
                // time (a name sort would rank the helper's prefix above the app's dates).
                var appLogs = Directory.EnumerateFiles(_paths.LogsDirectory, "optima-*.log")
                    .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                        Path.GetFileName(f), @"^optima-\d{8}\.log$"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2);
                var helperLogs = Directory.EnumerateFiles(_paths.LogsDirectory, "optima-watchdog-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(1);
                foreach (var log in appLogs.Concat(helperLogs))
                {
                    AddText("logs/" + Path.GetFileName(log), ReadShared(log));
                }

                var newestCrash = Directory.Exists(_paths.CrashesDirectory)
                    ? Directory.EnumerateDirectories(_paths.CrashesDirectory).OrderByDescending(d => d).FirstOrDefault()
                    : null;
                if (newestCrash is not null)
                {
                    foreach (var file in Directory.EnumerateFiles(newestCrash))
                    {
                        AddText("crash/" + Path.GetFileName(file), ReadShared(file));
                    }
                }

                foreach (var file in new[] { _paths.ConfigFile, _paths.DetectionFile })
                {
                    if (File.Exists(file))
                    {
                        AddText("settings/" + Path.GetFileName(file), ReadShared(file));
                    }
                }
            }

            try
            {
                Process.Start("explorer.exe", $"\"{supportDir}\"");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Opening the support folder failed; the path is in the status line");
            }
            return "archive ready: " + zipPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Support archive failed");
            return "support archive failed: " + ex.Message;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int CountPlatformProcesses(DetectionRules rules)
    {
        var count = 0;
        foreach (var pattern in rules.PlatformProcessPatterns.Concat(rules.EmulatorProcessPatterns))
        {
            var name = pattern.Trim('^', '$');
            var processes = Process.GetProcessesByName(name);
            count += processes.Length;
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        return count;
    }
}
