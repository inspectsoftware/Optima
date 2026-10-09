namespace Optima.Core.Configuration;

/// <summary>Well-known storage locations (§21).</summary>
public sealed class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\Optima, unless OPTIMA_DATA_DIR names another folder: a build under test can
    /// then run against a copy and leave the real settings, sessions and health marker alone.
    /// </summary>
    public AppPaths() : this(Environment.GetEnvironmentVariable("OPTIMA_DATA_DIR") is { Length: > 0 } overridden
        ? overridden
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optima"))
    {
    }

    public AppPaths(string root)
    {
        Root = root;
        ConfigFile = Path.Combine(root, "config.json");
        ProfilesFile = Path.Combine(root, "profiles.json");
        DetectionFile = Path.Combine(root, "detection.json");
        SessionsDatabase = Path.Combine(root, "sessions.db");
        LogsDirectory = Path.Combine(root, "logs");
        RecoveryDirectory = Path.Combine(root, "recovery");
        PendingSnapshotFile = Path.Combine(RecoveryDirectory, "pending-session.json");
        BackupsDirectory = Path.Combine(root, "backups");
        TweaksBackupFile = Path.Combine(BackupsDirectory, "tweaks-original-values.json");
        CrashesDirectory = Path.Combine(root, "crashes");
        HealthDirectory = Path.Combine(root, "health");
        IssuesFile = Path.Combine(HealthDirectory, "issues.json");
        VddRestoreMarkerFile = Path.Combine(BackupsDirectory, "vdd-settings.pending");
    }

    public string Root { get; }
    public string ConfigFile { get; }
    public string ProfilesFile { get; }
    public string DetectionFile { get; }
    public string SessionsDatabase { get; }
    public string LogsDirectory { get; }
    public string RecoveryDirectory { get; }
    public string PendingSnapshotFile { get; }
    public string BackupsDirectory { get; }

    public string TweaksBackupFile { get; }

    public string CrashesDirectory { get; }

    /// <summary>What Optima keeps about its own health: how the last run ended, and what went wrong in it.</summary>
    public string HealthDirectory { get; }

    /// <summary>What the issue list remembers between runs: the issues the user said to ignore.</summary>
    public string IssuesFile { get; }

    /// <summary>
    /// Left by a session that edited the virtual display's settings file: it names the backup to
    /// put back, and is removed when that has been done.
    /// </summary>
    public string VddRestoreMarkerFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CrashesDirectory);
        Directory.CreateDirectory(HealthDirectory);
    }
}
