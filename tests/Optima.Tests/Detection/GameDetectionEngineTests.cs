using Optima.Core.Abstractions;
using Optima.Core.Detection;
using Optima.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Detection;

public class GameDetectionEngineTests
{
    private sealed class FakeRegistry : IRegistryProbe
    {
        public Dictionary<string, Dictionary<string, string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string keyPath, string valueName)
            => Keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var value) ? value : null;

        public IReadOnlyList<string> GetSubKeyNames(string keyPath)
            => Keys.Keys
                .Where(k => k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[(keyPath.Length + 1)..].Split('\\')[0])
                .Distinct()
                .ToList();
    }

    private sealed class FakeFileSystem : IFileSystemProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
            => Files.Where(f => f.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase)
                && (pattern == "*.*" || f.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase))).ToList();
        public string? ReadAllText(string path) => FileContents.GetValueOrDefault(path);
    }

    private sealed class FakeProcesses : IProcessProbe
    {
        public List<(int Id, string Name)> Processes { get; } = [];
        public IReadOnlyList<(int Id, string Name)> GetProcesses() => Processes;
    }

    private sealed class FakeShortcuts : IShortcutResolver
    {
        public Dictionary<string, string> Uris { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ExtractUri(string shortcutPath) => Uris.GetValueOrDefault(shortcutPath);
        public string? GetDisplayName(string shortcutPath) => Path.GetFileNameWithoutExtension(shortcutPath);
    }

    private readonly FakeRegistry _registry = new();
    private readonly FakeFileSystem _fs = new();
    private readonly FakeProcesses _processes = new();
    private readonly FakeShortcuts _shortcuts = new();
    private DetectionRules _rules = new()
    {
        ShortcutFolders = [@"C:\StartMenu"],
        KnownInstallFolders = [@"C:\KnownFolder\Play Games"],
    };

    private GameDetectionEngine CreateEngine() => new(
        _registry, _fs, _processes, _shortcuts,
        _ => Task.FromResult(_rules),
        s => s, // no environment expansion in tests
        NullLogger<GameDetectionEngine>.Instance);

    [Fact]
    public async Task DetectPlatform_ViaRegistryUninstallEntry()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GPG";
        _registry.Keys[key] = new Dictionary<string, string>
        {
            ["DisplayName"] = "Google Play Games",
            ["InstallLocation"] = @"C:\Games\Play Games",
            ["DisplayVersion"] = "26.7.1101.2",
        };
        _fs.Directories.Add(@"C:\Games\Play Games");
        _fs.Files.Add(@"C:\Games\Play Games\Bootstrapper.exe");
        _processes.Processes.Add((100, "GooglePlayGamesServices"));

        var platform = await CreateEngine().DetectPlatformAsync();

        Assert.NotNull(platform);
        Assert.Equal(@"C:\Games\Play Games", platform.InstallDirectory);
        Assert.Equal("26.7.1101.2", platform.Version);
        Assert.Equal(@"C:\Games\Play Games\Bootstrapper.exe", platform.BootstrapperPath);
        Assert.True(platform.ServiceRunning);
    }

    [Fact]
    public async Task DetectPlatform_FallsBackToProtocolHandler()
    {
        _registry.Keys[@"HKCU\Software\Classes\googleplaygames\shell\open\command"] = new Dictionary<string, string>
        {
            [""] = "\"C:\\Handler\\Bootstrapper.exe\" \"%1\"",
        };
        _fs.Files.Add(@"C:\Handler\Bootstrapper.exe");
        _fs.Directories.Add(@"C:\Handler");

        var platform = await CreateEngine().DetectPlatformAsync();

        Assert.NotNull(platform);
        Assert.Equal(@"C:\Handler", platform.InstallDirectory);
        Assert.True(platform.ProtocolHandlerRegistered);
    }

    [Fact]
    public async Task DetectPlatform_FallsBackToKnownFolder()
    {
        _fs.Directories.Add(@"C:\KnownFolder\Play Games");

        var platform = await CreateEngine().DetectPlatformAsync();

        Assert.NotNull(platform);
        Assert.Equal(@"C:\KnownFolder\Play Games", platform.InstallDirectory);
    }

    [Fact]
    public async Task DetectPlatform_ManualPathWinsOverEverything()
    {
        _rules = _rules with { ManualInstallPath = @"D:\Custom\GPG" };
        _fs.Directories.Add(@"D:\Custom\GPG");
        _fs.Directories.Add(@"C:\KnownFolder\Play Games");

        var platform = await CreateEngine().DetectPlatformAsync();

        Assert.NotNull(platform);
        Assert.Equal(@"D:\Custom\GPG", platform.InstallDirectory);
    }

    [Fact]
    public async Task DetectPlatform_NothingFound_ReturnsNull()
    {
        Assert.Null(await CreateEngine().DetectPlatformAsync());
    }

    [Fact]
    public async Task DetectTargetGame_FindsCriticalOpsFromShortcuts()
    {
        _fs.Directories.Add(@"C:\StartMenu");
        _fs.Files.Add(@"C:\StartMenu\Critical Ops Multiplayer FPS.lnk");
        _fs.Files.Add(@"C:\StartMenu\Some Other Game.lnk");
        _fs.Files.Add(@"C:\StartMenu\NotAGame.lnk");
        _shortcuts.Uris[@"C:\StartMenu\Critical Ops Multiplayer FPS.lnk"] =
            "googleplaygames://launch/?id=com.criticalforceentertainment.criticalops&lid=2&pid=1";
        _shortcuts.Uris[@"C:\StartMenu\Some Other Game.lnk"] = "googleplaygames://launch/?id=com.other.game";

        var engine = CreateEngine();
        var games = await engine.DetectInstalledGamesAsync();
        var target = await engine.DetectTargetGameAsync();

        Assert.Equal(2, games.Count);
        Assert.NotNull(target);
        Assert.Equal("com.criticalforceentertainment.criticalops", target.PackageId);
        Assert.StartsWith("googleplaygames://launch/", target.LaunchUri);
    }

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" \"%1\"", "C:\\A B\\x.exe")]
    [InlineData("C:\\plain.exe %1", "C:\\plain.exe")]
    [InlineData("C:\\noargs.exe", "C:\\noargs.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractExecutableFromCommand_HandlesQuotingVariants(string? command, string? expected)
        => Assert.Equal(expected, GameDetectionEngine.ExtractExecutableFromCommand(command));

    [Theory]
    [InlineData("googleplaygames://launch/?id=com.foo.bar&lid=2", "com.foo.bar")]
    [InlineData("googleplaygames://launch/?x=1&id=com.foo_2.bar", "com.foo_2.bar")]
    [InlineData("googleplaygames://launch/", null)]
    public void ExtractPackageId_ParsesUriVariants(string uri, string? expected)
        => Assert.Equal(expected, GameDetectionEngine.ExtractPackageId(uri));

    [Fact]
    public void MatchesAny_UsesRegexSemantics()
    {
        string[] patterns = ["^crosvm$"];
        Assert.True(GameDetectionEngine.MatchesAny("crosvm", patterns));
        Assert.True(GameDetectionEngine.MatchesAny("CROSVM", patterns));
        Assert.False(GameDetectionEngine.MatchesAny("crosvm2", patterns));
    }
}
