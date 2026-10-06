namespace Optima.Core.Configuration;

/// <summary>Well-known storage locations (§21).</summary>
public sealed class AppPaths
{
    public AppPaths() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optima"))
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
