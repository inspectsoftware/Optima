using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Crashes;

/// <summary>
/// The Watchdog's crash arm: on every game exit it reads the platform's own logcat tail and, only when a real failure
/// marker is present, writes a crash bundle under %LOCALAPPDATA%\Optima\crashes.
/// </summary>
public sealed class CrashSentinel : IDisposable
{
    private readonly GamePresenceService _presence;
    private readonly GpgLogReader _reader;
    private readonly AppPaths _paths;
    private readonly Func<CancellationToken, Task<DetectionRules>> _rules;
    private readonly ILogger<CrashSentinel> _logger;
    private bool _subscribed;

    public CrashSentinel(
        GamePresenceService presence,
        GpgLogReader reader,
        AppPaths paths,
        Func<CancellationToken, Task<DetectionRules>> rules,
        ILogger<CrashSentinel> logger)
    {
        _presence = presence;
        _reader = reader;
        _paths = paths;
        _rules = rules;
        _logger = logger;
    }

    public event Action<string>? BundleWritten;

    public void Start()
    {
        if (!_subscribed)
        {
            _presence.GameExited += OnGameExited;
            _subscribed = true;
        }
    }

    private void OnGameExited(GameExit exit)
        => _ = Task.Run(() => CaptureAsync(exit, manual: false, CancellationToken.None));

    public Task<string?> CaptureManualAsync(CancellationToken ct = default)
        => CaptureAsync(exit: null, manual: true, ct);

    private async Task<string?> CaptureAsync(GameExit? exit, bool manual, CancellationToken ct)
    {
        try
        {
            var rules = await _rules(ct).ConfigureAwait(false);
            var lines = _reader.ReadRecentSerialLines();
            var evidence = CrashSignals.Extract(lines, rules.GamePackageId);

            if (!manual && !CrashSignals.ShouldCapture(evidence))
            {
                _logger.LogDebug("Game exit had no crash markers; nothing captured");
                return null;
            }

            var folder = Path.Combine(_paths.CrashesDirectory, "crash-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);

            var minidumps = _reader.ListMinidumpNames();
            var report = BuildReport(exit, manual, evidence, minidumps);
            await File.WriteAllTextAsync(Path.Combine(folder, "report.txt"), report, ct).ConfigureAwait(false);
            await File.WriteAllLinesAsync(Path.Combine(folder, "androidserial-excerpt.log"), evidence.ExcerptLines, ct).ConfigureAwait(false);

            _logger.LogInformation("Crash bundle written: {Folder} (fatal: {Fatal})", folder, evidence.FatalSeen);
            BundleWritten?.Invoke(folder);
            return folder;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Crash capture failed");
            return null;
        }
    }

    private string BuildReport(GameExit? exit, bool manual, CrashEvidence evidence, IReadOnlyList<string> minidumps)
    {
        var version = typeof(CrashSentinel).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var writer = new System.Text.StringBuilder();
        writer.AppendLine("Optima crash report");
        writer.AppendLine("===================");
        writer.AppendLine($"captured: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        writer.AppendLine($"trigger: {(manual ? "manual capture" : "game exit with crash markers")}");
        writer.AppendLine($"optima version: {version}");
        writer.AppendLine($"windows: {Environment.OSVersion.VersionString} (64-bit: {Environment.Is64BitOperatingSystem})");
        writer.AppendLine();
        if (exit is not null)
        {
            writer.AppendLine($"game run ended: {exit.At:HH:mm:ss}");
            writer.AppendLine($"run duration: {exit.RunDuration:hh\\:mm\\:ss}");
            writer.AppendLine($"emulator still alive at exit: {exit.EmulatorStillAlive}");
        }
        writer.AppendLine($"fatal markers in logcat: {evidence.FatalSeen}");
        writer.AppendLine($"force-stop seen: {evidence.ForceStopSeen}");
        writer.AppendLine($"excerpt lines: {evidence.ExcerptLines.Count} (androidserial-excerpt.log)");
        writer.AppendLine();
        writer.AppendLine("platform minidumps (referenced only; the files stay in Google's CrashReporting folder):");
        if (minidumps.Count == 0)
        {
            writer.AppendLine("  none found");
        }
        foreach (var dump in minidumps)
        {
            writer.AppendLine("  " + dump);
        }
        writer.AppendLine();
        writer.AppendLine("Share the redacted export of this bundle with the developers; the raw bundle can");
        writer.AppendLine("contain your Windows user name inside file paths.");
        return writer.ToString();
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _presence.GameExited -= OnGameExited;
            _subscribed = false;
        }
    }
}
