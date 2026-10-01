using System.Diagnostics;
using System.Text.RegularExpressions;
using Optima.Core.Abstractions;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Launchers;

/// <summary>
/// Opt-in strategy: start the game inside the Google Play Games Developer Emulator instead of the
/// consumer client. The developer build exposes no protocol handler of its own, so the emulator is
/// booted directly and the game is started through adb on localhost:6520, the documented
/// development flow (adb shell monkey -p &lt;package&gt; 1).
/// </summary>
public sealed class DeveloperEmulatorLauncher : IGameLauncher
{
    /// <summary>The adb endpoint the developer emulator always listens on (§developer docs).</summary>
    public const string DefaultAdbEndpoint = "localhost:6520";

    private readonly IGameDetector _detector;
    private readonly Func<CancellationToken, Task<AppSettings>> _settingsProvider;
    private readonly Func<CancellationToken, Task<DetectionRules>> _rulesProvider;
    private readonly ILogger<DeveloperEmulatorLauncher> _logger;

    public DeveloperEmulatorLauncher(
        IGameDetector detector,
        Func<CancellationToken, Task<AppSettings>> settingsProvider,
        Func<CancellationToken, Task<DetectionRules>> rulesProvider,
        ILogger<DeveloperEmulatorLauncher> logger)
    {
        _detector = detector;
        _settingsProvider = settingsProvider;
        _rulesProvider = rulesProvider;
        _logger = logger;
    }

    public string Name => "Developer emulator (adb)";
    public int Order => 15;

    // Opt-in and exclusive: only claims the session when the user enabled it in Settings, and a
    // failed claim must not fall through to the consumer client (the two clients cannot run at
    // the same time, so a silent fallback would leave a half-booted developer emulator behind).
    public bool IsEnabled { get; private set; }

    public bool IsExclusive => true;

    public async Task<bool> CanLaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        var settings = await _settingsProvider(ct).ConfigureAwait(false);
        IsEnabled = settings.UseDeveloperEmulator;
        if (!IsEnabled)
        {
            return false;
        }

        var adb = await ResolveAdbPathAsync(ct).ConfigureAwait(false);
        if (adb is null)
        {
            _logger.LogWarning(
                "Developer emulator launch requested but adb.exe was not found under the developer emulator install");
            return false;
        }

        return !string.IsNullOrWhiteSpace(game.PackageId);
    }

    public async Task<bool> LaunchAsync(InstalledGame game, CancellationToken ct = default)
    {
        var adb = await ResolveAdbPathAsync(ct).ConfigureAwait(false);
        if (adb is null || string.IsNullOrWhiteSpace(game.PackageId))
        {
            return false;
        }

        try
        {
            // Bootstrapper.exe starts the developer emulator itself (the dev build has no client
            // process and registers no protocol handler). A no-op if it is already running.
            var bootstrapper = await ResolveBootstrapperPathAsync(ct).ConfigureAwait(false);
            if (bootstrapper is not null)
            {
                _logger.LogInformation("Starting developer emulator via {Bootstrapper}", bootstrapper);
                using (var boot = Process.Start(new ProcessStartInfo(bootstrapper)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(bootstrapper)!,
                }))
                {
                    // Fire and forget: the emulator keeps booting after this handle closes.
                }
            }
            else
            {
                _logger.LogWarning("Developer emulator Bootstrapper.exe not found; assuming the emulator is already running");
            }

            // The VM needs a moment before adb accepts a connection; a few short retries cover
            // both cold boot and an emulator that was already up.
            if (!await WaitForDeviceAsync(adb, ct).ConfigureAwait(false))
            {
                _logger.LogError("adb never reported a device at {Endpoint}", DefaultAdbEndpoint);
                return false;
            }

            // monkey starts the package's launcher activity without needing to know its name.
            var monkey = await RunAdbAsync(adb, $"shell monkey -p {game.PackageId} 1", ct).ConfigureAwait(false);
            if (!monkey.Success)
            {
                _logger.LogError("adb monkey failed for {Package}: {Output}", game.PackageId, monkey.Output);
                return false;
            }

            _logger.LogInformation("Critical Ops started in the developer emulator (adb localhost:6520)");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Developer emulator launch failed");
            return false;
        }
    }

    /// <summary>Builds one adb argument line; public and pure for tests.</summary>
    public static string BuildConnectArgs(string endpoint = DefaultAdbEndpoint) => $"connect {endpoint}";

    public static string BuildMonkeyArgs(string packageId, string endpoint = DefaultAdbEndpoint)
        => $"-s {endpoint} shell monkey -p {packageId} 1";

    private async Task<string?> ResolveAdbPathAsync(CancellationToken ct)
    {
        var root = await ResolveInstallRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var adb = Path.Combine(root, "current", "emulator", "adb.exe");
        return File.Exists(adb) ? adb : null;
    }

    private async Task<string?> ResolveBootstrapperPathAsync(CancellationToken ct)
    {
        var root = await ResolveInstallRootAsync(ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var bootstrapper = Path.Combine(root, "Bootstrapper.exe");
        return File.Exists(bootstrapper) ? bootstrapper : null;
    }

    /// <summary>Developer emulator root: Settings override, then the standard folder, then the uninstall registry entry.</summary>
    private async Task<string?> ResolveInstallRootAsync(CancellationToken ct)
    {
        var rules = await _rulesProvider(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(rules.DeveloperEmulatorInstallPath)
            && Directory.Exists(rules.DeveloperEmulatorInstallPath))
        {
            return rules.DeveloperEmulatorInstallPath;
        }

        var platform = await _detector.DetectPlatformAsync(ct).ConfigureAwait(false);
        if (platform is not null
            && platform.InstallDirectory.Contains("Developer Emulator", StringComparison.OrdinalIgnoreCase))
        {
            return platform.InstallDirectory;
        }

        var fallback = Environment.ExpandEnvironmentVariables(
            @"%ProgramFiles%\Google\Play Games Developer Emulator");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private async Task<bool> WaitForDeviceAsync(string adb, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // 'connect' is idempotent: it succeeds when already attached.
            await RunAdbAsync(adb, BuildConnectArgs(), ct).ConfigureAwait(false);

            var state = await RunAdbAsync(adb, $"-s {DefaultAdbEndpoint} get-state", ct).ConfigureAwait(false);
            if (state.Success && state.Output.Trim() == "device")
            {
                _logger.LogInformation("adb device ready after {Attempts} attempt(s)", attempt);
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<(bool Success, string Output)> RunAdbAsync(string adb, string arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(adb, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return (false, string.Empty);
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var combined = string.Join(Environment.NewLine,
            new[] { output.TrimEnd(), error.TrimEnd() }.Where(s => s.Length > 0));
        _logger.LogInformation("adb {Arguments} -> exit {ExitCode}: {Output}", arguments, process.ExitCode, combined);
        return (process.ExitCode == 0, combined);
    }
}
