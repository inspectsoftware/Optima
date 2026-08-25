namespace Optima.Core.Models;

/// <summary>
/// Config-driven detection rules (§29). Shipped as defaults, overridable via
/// %LOCALAPPDATA%\Optima\detection.json so Google Play Games updates
/// can be accommodated without a new build.
/// </summary>
public sealed record DetectionRules
{
    /// <summary>Registry uninstall keys probed for the Google Play Games entry.</summary>
    public IReadOnlyList<string> UninstallKeyPaths { get; init; } =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>DisplayName regex that identifies the Google Play Games uninstall entry.</summary>
    public string UninstallDisplayNamePattern { get; init; } = "^Google Play Games";

    /// <summary>Known install folders probed when the registry yields nothing.</summary>
    public IReadOnlyList<string> KnownInstallFolders { get; init; } =
    [
        @"%ProgramFiles%\Google\Play Games",
        @"%ProgramFiles(x86)%\Google\Play Games",
    ];

    /// <summary>Protocol scheme whose handler resolves the bootstrapper executable.</summary>
    public string ProtocolScheme { get; init; } = "googleplaygames";

    /// <summary>Regex patterns (no extension) matching Google Play Games platform processes.</summary>
    public IReadOnlyList<string> PlatformProcessPatterns { get; init; } =
    [
        "^GooglePlayGamesServices$",
        "^Bootstrapper$",
        "^client$",
        "^Service$",
    ];

    /// <summary>Regex patterns matching the Android VM process that hosts the game.</summary>
    public IReadOnlyList<string> EmulatorProcessPatterns { get; init; } = ["^crosvm$"];

    /// <summary>Package id of the game this bootstrapper targets.</summary>
    public string GamePackageId { get; init; } = "com.criticalforceentertainment.criticalops";

    /// <summary>Substring expected in the game window title while running.</summary>
    public string GameWindowTitlePattern { get; init; } = "Critical Ops";

    /// <summary>Start-menu folders scanned for googleplaygames:// shortcuts.</summary>
    public IReadOnlyList<string> ShortcutFolders { get; init; } =
    [
        @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Google Play Games",
        @"%ProgramData%\Microsoft\Windows\Start Menu\Programs\Google Play Games",
        @"%USERPROFILE%\Desktop",
    ];

    /// <summary>User-supplied fallback path to the Google Play Games install folder.</summary>
    public string? ManualInstallPath { get; init; }

    /// <summary>User-supplied fallback launch command (§5, custom launch strategy).</summary>
    public string? CustomLaunchCommand { get; init; }
}
