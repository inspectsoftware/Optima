namespace Optima.Core.Models;

/// <summary>Config-driven detection rules (§29).</summary>
public sealed record DetectionRules
{
    public IReadOnlyList<string> UninstallKeyPaths { get; init; } =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public string UninstallDisplayNamePattern { get; init; } = "^Google Play Games";

    public IReadOnlyList<string> KnownInstallFolders { get; init; } =
    [
        @"%ProgramFiles%\Google\Play Games",
        @"%ProgramFiles(x86)%\Google\Play Games",
    ];

    public string ProtocolScheme { get; init; } = "googleplaygames";

    public IReadOnlyList<string> PlatformProcessPatterns { get; init; } =
    [
        "^GooglePlayGamesServices$",
        "^Bootstrapper$",
        "^client$",
        "^Service$",
    ];

    public IReadOnlyList<string> EmulatorProcessPatterns { get; init; } = ["^crosvm$"];

    public string GamePackageId { get; init; } = "com.criticalforceentertainment.criticalops";

    public string GameWindowTitlePattern { get; init; } = "Critical Ops";

    public IReadOnlyList<string> ShortcutFolders { get; init; } =
    [
        @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Google Play Games",
        @"%ProgramData%\Microsoft\Windows\Start Menu\Programs\Google Play Games",
        @"%USERPROFILE%\Desktop",
    ];

    public string? ManualInstallPath { get; init; }

    public string? CustomLaunchCommand { get; init; }
}
