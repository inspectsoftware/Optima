using System.Diagnostics;
using COPSBootstrapper.Core.Abstractions;
using COPSBootstrapper.Core.Models;
using Microsoft.Extensions.Logging;

namespace COPSBootstrapper.Platform.Windows.Launchers;

/// <summary>
/// Strategy 1 (§5): ShellExecute the googleplaygames:// launch URI so Windows routes it through
/// the registered protocol handler — the same path the official Start-menu shortcut takes.
/// </summary>
public sealed class ProtocolUriLauncher : IGameLauncher
{
    private readonly ILogger<ProtocolUriLauncher> _logger;

    public ProtocolUriLauncher(ILogger<ProtocolUriLauncher> logger)
    {
        _logger = logger;
    }

    public string Name => "Protocol URI";
    public int Order => 10;

    public Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(game.LaunchUri));

    public Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Protocol URI launch failed for {Uri}", game.LaunchUri);
            return Task.FromResult(false);
        }
    }
}

/// <summary>Strategy 2: invoke Bootstrapper.exe directly with the launch URI as its argument.</summary>
public sealed class BootstrapperExeLauncher : IGameLauncher
{
    private readonly IGameDetector _detector;
    private readonly ILogger<BootstrapperExeLauncher> _logger;

    public BootstrapperExeLauncher(IGameDetector detector, ILogger<BootstrapperExeLauncher> logger)
    {
        _detector = detector;
        _logger = logger;
    }

    public string Name => "Bootstrapper executable";
    public int Order => 20;

    public async Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchUri))
        {
            return false;
        }
        var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(platform?.BootstrapperPath);
    }

    public async Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
        if (platform is null || string.IsNullOrEmpty(platform.BootstrapperPath))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(platform.BootstrapperPath, $"\"{game.LaunchUri}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = platform.InstallDirectory,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Bootstrapper launch failed");
            return false;
        }
    }
}

/// <summary>Strategy 3: ShellExecute the game's own Start-menu / desktop shortcut.</summary>
public sealed class ShortcutLauncher : IGameLauncher
{
    private readonly ILogger<ShortcutLauncher> _logger;

    public ShortcutLauncher(ILogger<ShortcutLauncher> logger)
    {
        _logger = logger;
    }

    public string Name => "Game shortcut";
    public int Order => 30;

    public Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(game.ShortcutPath) && File.Exists(game.ShortcutPath));

    public Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(game.ShortcutPath) { UseShellExecute = true });
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Shortcut launch failed for {Path}", game.ShortcutPath);
            return Task.FromResult(false);
        }
    }
}

/// <summary>Strategy 4: a user-configured command line; "{uri}" and "{package}" are substituted.</summary>
public sealed class CustomCommandLauncher : IGameLauncher
{
    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;
    private readonly ILogger<CustomCommandLauncher> _logger;

    public CustomCommandLauncher(Func<CancellationToken, Task<DetectionRules>> rulesProvider, ILogger<CustomCommandLauncher> logger)
    {
        _rulesProvider = rulesProvider;
        _logger = logger;
    }

    public string Name => "Custom command";
    public int Order => 40;

    public async Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(rules.CustomLaunchCommand);
    }

    public async Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        var command = rules.CustomLaunchCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        command = command
            .Replace("{uri}", game.LaunchUri, StringComparison.OrdinalIgnoreCase)
            .Replace("{package}", game.PackageId, StringComparison.OrdinalIgnoreCase);

        var (exe, args) = SplitCommand(command);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Custom launch command failed: {Command}", command);
            return false;
        }
    }

    public static (string Exe, string Args) SplitCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                return (trimmed[1..end], trimmed[(end + 1)..].TrimStart());
            }
        }
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..]);
    }
}
