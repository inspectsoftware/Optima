using System.Text.RegularExpressions;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.Core.Detection;

/// <summary>
/// Rule-driven detection (§4/§29). All OS access goes through the injected probes so the
/// evaluation order — registry → known folders → protocol handler → processes → manual path —
/// is unit-testable with fakes. Platform.Windows provides the real probes.
/// </summary>
public sealed class GameDetectionEngine : IGameDetector
{
    private readonly IRegistryProbe _registry;
    private readonly IFileSystemProbe _fs;
    private readonly IProcessProbe _processes;
    private readonly IShortcutResolver _shortcuts;
    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;
    private readonly Func<string, string> _expandEnvironment;
    private readonly ILogger<GameDetectionEngine> _logger;

    public GameDetectionEngine(
        IRegistryProbe registry,
        IFileSystemProbe fs,
        IProcessProbe processes,
        IShortcutResolver shortcuts,
        Func<CancellationToken, Task<DetectionRules>> rulesProvider,
        Func<string, string> expandEnvironment,
        ILogger<GameDetectionEngine> logger)
    {
        _registry = registry;
        _fs = fs;
        _processes = processes;
        _shortcuts = shortcuts;
        _rulesProvider = rulesProvider;
        _expandEnvironment = expandEnvironment;
        _logger = logger;
    }

    public async Task<GooglePlayGamesInstallation?> DetectPlatformAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);

        var (installDir, version) = FindInstallDirectory(rules);
        if (installDir is null)
        {
            _logger.LogWarning("Google Play Games installation not found by any strategy");
            return null;
        }

        var bootstrapper = Path.Combine(installDir, "Bootstrapper.exe");
        var client = Path.Combine(installDir, "current", "client", "client.exe");
        var emulator = Path.Combine(installDir, "current", "emulator", "crosvm.exe");

        var running = _processes.GetProcesses();
        var serviceRunning = running.Any(p => MatchesAny(p.Name, rules.PlatformProcessPatterns));

        var installation = new GooglePlayGamesInstallation
        {
            InstallDirectory = installDir,
            Version = version ?? TryVersionFromManifest(installDir) ?? string.Empty,
            BootstrapperPath = _fs.FileExists(bootstrapper) ? bootstrapper : string.Empty,
            ClientPath = _fs.FileExists(client) ? client : string.Empty,
            EmulatorPath = _fs.FileExists(emulator) ? emulator : string.Empty,
            ProtocolHandlerRegistered = ResolveProtocolHandler(rules) is not null,
            ServiceRunning = serviceRunning,
        };

        _logger.LogInformation("Google Play Games detected at {Dir} (version {Version}, service running: {Running})",
            installDir, installation.Version, serviceRunning);
        return installation;
    }

    public async Task<IReadOnlyList<InstalledGame>> DetectInstalledGamesAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var games = new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);

        foreach (var folderTemplate in rules.ShortcutFolders)
        {
            ct.ThrowIfCancellationRequested();
            var folder = _expandEnvironment(folderTemplate);
            if (!_fs.DirectoryExists(folder))
            {
                continue;
            }

            foreach (var lnk in _fs.EnumerateFiles(folder, "*.lnk"))
            {
                var uri = _shortcuts.ExtractUri(lnk);
                if (uri is null || !uri.StartsWith(rules.ProtocolScheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var packageId = ExtractPackageId(uri);
                if (packageId is null || games.ContainsKey(packageId))
                {
                    continue;
                }

                games[packageId] = new InstalledGame
                {
                    PackageId = packageId,
                    DisplayName = _shortcuts.GetDisplayName(lnk) ?? Path.GetFileNameWithoutExtension(lnk),
                    LaunchUri = uri,
                    ShortcutPath = lnk,
                };
            }
        }

        _logger.LogInformation("Installed Google Play Games titles found: {Count}", games.Count);
        return games.Values.ToList();
    }

    public async Task<InstalledGame?> DetectTargetGameAsync(CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var games = await DetectInstalledGamesAsync(ct).ConfigureAwait(false);
        var game = games.FirstOrDefault(g => string.Equals(g.PackageId, rules.GamePackageId, StringComparison.OrdinalIgnoreCase));

        if (game is null)
        {
            _logger.LogWarning("Target game {Package} not found among installed titles", rules.GamePackageId);
        }
        else
        {
            _logger.LogInformation("Critical Ops launcher resolved: {Uri}", game.LaunchUri);
        }
        return game;
    }

    /// <summary>The executable registered for the googleplaygames:// protocol, or null.</summary>
    public string? ResolveProtocolHandlerExecutable(DetectionRules rules) => ResolveProtocolHandler(rules);

    private (string? Dir, string? Version) FindInstallDirectory(DetectionRules rules)
    {
        // 1. Manual override wins.
        if (!string.IsNullOrWhiteSpace(rules.ManualInstallPath))
        {
            var manual = _expandEnvironment(rules.ManualInstallPath);
            if (_fs.DirectoryExists(manual))
            {
                return (manual, null);
            }
            _logger.LogWarning("Configured manual install path does not exist: {Path}", manual);
        }

        // 2. Registry uninstall entries.
        var namePattern = new Regex(rules.UninstallDisplayNamePattern, RegexOptions.IgnoreCase);
        foreach (var keyPath in rules.UninstallKeyPaths)
        {
            foreach (var sub in _registry.GetSubKeyNames(keyPath))
            {
                var subKey = $@"{keyPath}\{sub}";
                var name = _registry.GetValue(subKey, "DisplayName");
                if (name is null || !namePattern.IsMatch(name))
                {
                    continue;
                }

                var location = _registry.GetValue(subKey, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(location) && _fs.DirectoryExists(location))
                {
                    return (location, _registry.GetValue(subKey, "DisplayVersion"));
                }
            }
        }

        // 3. Protocol handler → executable directory.
        if (ResolveProtocolHandler(rules) is { } handlerExe)
        {
            var dir = Path.GetDirectoryName(handlerExe);
            if (dir is not null && _fs.DirectoryExists(dir))
            {
                return (dir, null);
            }
        }

        // 4. Known folders.
        foreach (var template in rules.KnownInstallFolders)
        {
            var folder = _expandEnvironment(template);
            if (_fs.DirectoryExists(folder))
            {
                return (folder, null);
            }
        }

        return (null, null);
    }

    private string? ResolveProtocolHandler(DetectionRules rules)
    {
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            var command = _registry.GetValue($@"{hive}\Software\Classes\{rules.ProtocolScheme}\shell\open\command", "");
            var exe = ExtractExecutableFromCommand(command);
            if (exe is not null && _fs.FileExists(exe))
            {
                return exe;
            }
        }
        return null;
    }

    private string? TryVersionFromManifest(string installDir)
    {
        // current\client\manifest.xml carries the client version in recent GPG builds.
        var manifest = Path.Combine(installDir, "current", "client", "manifest.xml");
        var text = _fs.ReadAllText(manifest);
        if (text is null)
        {
            return null;
        }
        var match = Regex.Match(text, @"version=""(?<v>[\d.]+)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    public static string? ExtractExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    public static string? ExtractPackageId(string uri)
    {
        // googleplaygames://launch/?id=com.example.game&lid=2&pid=1
        var match = Regex.Match(uri, @"[?&]id=(?<id>[A-Za-z0-9._]+)");
        return match.Success ? match.Groups["id"].Value : null;
    }

    public static bool MatchesAny(string processName, IReadOnlyList<string> patterns)
        => patterns.Any(p => Regex.IsMatch(processName, p, RegexOptions.IgnoreCase));
}
