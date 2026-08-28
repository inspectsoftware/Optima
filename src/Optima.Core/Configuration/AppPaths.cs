namespace Optima.Core.Configuration;

/// <summary>Well-known storage locations (§21). Everything lives under %LOCALAPPDATA%\Optima.</summary>
public sealed class AppPaths
{
    public AppPaths() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optima"))
    {
    }

    /// <summary>Test constructor. Points all paths at an arbitrary root.</summary>
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

    /// <summary>Original registry values captured before a Windows tweak is first applied.</summary>
    public string TweaksBackupFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
