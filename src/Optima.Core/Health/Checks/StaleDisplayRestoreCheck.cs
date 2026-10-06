using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;

namespace Optima.Core.Health.Checks;

/// <summary>
/// A restore of the virtual display's settings file that was promised and never ran. A session that
/// edits vdd_settings.xml leaves a marker naming the backup to put back; the marker is cleared when
/// the backup is restored. One that is days old means a session ended without restoring, and the
/// next virtual display session will copy that old backup over whatever the file holds by then,
/// without saying so. Finding it here turns a silent overwrite into a decision.
/// </summary>
public sealed class StaleDisplayRestoreCheck : IDiagnosticCheck
{
    /// <summary>A marker younger than this belongs to a session that is running or has just ended.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly AppPaths _paths;
    private readonly Func<bool> _sessionActive;
    private readonly Func<DateTimeOffset> _clock;

    public StaleDisplayRestoreCheck(AppPaths paths, Func<bool> sessionActive, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _sessionActive = sessionActive;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Name => "Pending Display Restore";
    public int Order => 55;
    public CheckScope Scope => CheckScope.Startup | CheckScope.Preflight;

    public Task<DiagnosticResult> RunAsync(CancellationToken ct = default)
    {
        var marker = _paths.VddRestoreMarkerFile;
        if (!File.Exists(marker))
        {
            return Task.FromResult(Result(DiagnosticStatus.Pass, "No restore of the virtual display settings is waiting."));
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(marker), TimeSpan.Zero);
        var age = _clock() - written;
        if (_sessionActive() || age < StaleAfter)
        {
            return Task.FromResult(Result(DiagnosticStatus.Pass, "A session's restore marker is in place, as it should be while one runs."));
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines = [];
        }
        var backup = lines.Length > 0 ? lines[0] : "(unreadable)";
        var target = lines.Length > 1 ? lines[1] : "(unreadable)";

        return Task.FromResult(new DiagnosticResult
        {
            CheckName = Name,
            Status = DiagnosticStatus.Warning,
            Reason = $"A restore of the virtual display settings from {written.ToLocalTime():yyyy-MM-dd} never ran.",
            RecommendedFix = "The next virtual display session will copy that backup over the settings file. "
                + "If the file has been changed since and should stay as it is, delete the marker first.",
            Details = $"marker: {marker}\nbackup: {backup}\nwould overwrite: {target}",
            IssueCode = "VDD_RESTORE_PENDING",
        });
    }

    private DiagnosticResult Result(DiagnosticStatus status, string reason) => new()
    {
        CheckName = Name,
        Status = status,
        Reason = reason,
        IssueCode = "VDD_RESTORE_PENDING",
    };
}
