using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Optima.Core.Models;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Services;

/// <summary>High-performance / power-saving / auto, matching the values Windows accepts in
/// the per-application GPU preference string.</summary>
public enum GpuPreferenceKind
{
    Auto = 0,
    PowerSaving = 1,
    HighPerformance = 2,
}

/// <summary>State of one Google startup flag (the startup_flags marker files).</summary>
public sealed record DevEmulatorFlagState(string Name, bool Present, string MarkerState, int Version);

/// <summary>Everything the DEVELOPER EMULATOR settings section shows, read fresh from disk and registry.</summary>
public sealed record DevEmulatorSnapshot
{
    public bool Installed { get; init; }
    public string? InstallRoot { get; init; }
    public string? Version { get; init; }
    public bool EmulatorRunning { get; init; }
    public GpuPreferenceKind WindowsGpuPreference { get; init; }
    public string? WindowsGpuPreferenceRaw { get; init; }
    public DevEmulatorFlagState GpuPrioritization { get; init; } = new(GpuPrioritizationFlag, false, "absent", 0);
    public DevEmulatorFlagState GpuRetrievalRetry { get; init; } = new(GpuRetrievalRetryFlag, false, "absent", 0);
    public IReadOnlyList<Optima.Core.Models.GpuInfo> Gpus { get; init; } = [];
    public string? AdbPath { get; init; }

    public const string GpuPrioritizationFlag = "enable_idxgi_factory6_gpu_prioritization";
    public const string GpuRetrievalRetryFlag = "enable_gpu_retrieval_retry_on_mismatch";
}

/// <summary>
/// Controls the Google Play Games Developer Emulator settings that live outside Google's own UI:
/// the Windows per-application GPU preference for its crosvm.exe (the OS-level "prioritize GPU"
/// lever), Google's startup flags (marker files under startup_flags, including the IDXGI factory 6
/// GPU prioritization flag), run state over adb, and the GPU inventory.
///
/// The emulator's own Vulkan/DirectX backend toggle and aspect-ratio settings are protobuf blobs in
/// Google's store.db and are deliberately not touched here.
/// </summary>
public sealed partial class DevEmulatorSettingsService
{
    /// <summary>The adb endpoint the developer emulator always listens on.</summary>
    public const string AdbEndpoint = "localhost:6520";

    public const string CriticalOpsPackage = "com.criticalforceentertainment.criticalops";

    /// <summary>Android global-setting names that force ANGLE as the GL driver for a package.</summary>
    public const string AnglePkgSetting = "angle_gl_driver_selection_pkgs";
    public const string AngleValueSetting = "angle_gl_driver_selection_values";
    public const string AngleDriverValue = "angle";

    private const string GpuPreferencesKeyName = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DevEmulatorDataFolder = @"Google\Play Games Developer Emulator";
    private const string UninstallKeyName = @"SOFTWARE\Google\Play Games Developer Emulator";

    private readonly ILogger<DevEmulatorSettingsService> _logger;

    public DevEmulatorSettingsService(ILogger<DevEmulatorSettingsService> logger)
    {
        _logger = logger;
    }

    public async Task<DevEmulatorSnapshot> ReadAsync(string? installRootOverride = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var root = ResolveInstallRoot(installRootOverride);
        var version = ReadInstalledVersion();
        var crosvm = root is null ? null : Path.Combine(root, "current", "emulator", "crosvm.exe");
        var adb = root is null ? null : Path.Combine(root, "current", "emulator", "adb.exe");

        var preference = crosvm is null || !File.Exists(crosvm)
            ? (GpuPreferenceKind.Auto, (string?)null)
            : ParseGpuPreference(ReadGpuPreferenceValue(crosvm));

        return new DevEmulatorSnapshot
        {
            Installed = root is not null,
            InstallRoot = root,
            Version = version,
            EmulatorRunning = IsEmulatorRunning(root),
            WindowsGpuPreference = preference.Item1,
            WindowsGpuPreferenceRaw = preference.Item2,
            GpuPrioritization = ReadFlagState(DevEmulatorSnapshot.GpuPrioritizationFlag),
            GpuRetrievalRetry = ReadFlagState(DevEmulatorSnapshot.GpuRetrievalRetryFlag),
            Gpus = await EnumerateGpusAsync(ct).ConfigureAwait(false),
            AdbPath = adb is not null && File.Exists(adb) ? adb : null,
        };
    }

    /// <summary>Writes the Windows per-app GPU preference for the developer emulator's crosvm.exe.
    /// Takes effect the next time the emulator starts. Returns false when the install was not found.</summary>
    public Task<bool> SetWindowsGpuPreferenceAsync(GpuPreferenceKind kind, string? installRootOverride = null)
    {
        var crosvm = ResolveCrosvmPath(installRootOverride);
        if (crosvm is null || !File.Exists(crosvm))
        {
            _logger.LogWarning("Cannot set GPU preference: developer emulator crosvm.exe not found");
            return Task.FromResult(false);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesKeyName, writable: true);
            if (key is null)
            {
                return Task.FromResult(false);
            }

            var current = ReadGpuPreferenceValue(crosvm);
            var updated = ApplyGpuPreference(current, kind);
            if (updated.Length == 0)
            {
                // Nothing left for this app: remove the value entirely, like the Windows UI does.
                key.DeleteValue(crosvm, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(crosvm, updated, RegistryValueKind.String);
            }

            _logger.LogInformation("GPU preference for {Crosvm} set to {Kind} (was: {Previous})", crosvm, kind, current);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write the per-app GPU preference for {Crosvm}", crosvm);
            return Task.FromResult(false);
        }
    }

    /// <summary>Creates or removes a startup flag marker. Google's StartupPathFlagsModule reads these
    /// files on service start; flags it does not know may be ignored or reset by an emulator update.
    /// The emulator must not be running: it rewrites the markers while it runs.</summary>
    public Task SetStartupFlagAsync(string flagName, bool enabled, string? installRootOverride = null)
    {
        if (IsEmulatorRunning(ResolveInstallRoot(installRootOverride)))
        {
            throw new InvalidOperationException(
                "The Google Play Games Developer Emulator is running. Quit it from the system tray first — it rewrites these flags while it runs.");
        }

        var folder = StartupFlagsFolder();
        Directory.CreateDirectory(folder);

        if (!enabled)
        {
            var removed = 0;
            foreach (var marker in Directory.EnumerateFiles(folder, flagName + ".*"))
            {
                File.Delete(marker);
                removed++;
            }
            _logger.LogInformation("Startup flag {Flag} cleared ({Removed} marker files removed)", flagName, removed);
            return Task.CompletedTask;
        }

        // Keep the existing version/series when the flag was written before, so Google's module
        // keeps recognizing it; otherwise mint a fresh one.
        var existing = ReadFlagState(flagName);
        var version = existing.Version > 0 ? existing.Version : 1;
        var state = existing.Present && existing.MarkerState is "TENTATIVE" or "COMMITTED"
            ? existing.MarkerState
            : "TENTATIVE";
        var path = Path.Combine(folder, $"{flagName}.v{version}.{state}.01");
        File.WriteAllText(path, string.Empty);
        _logger.LogInformation("Startup flag {Flag} enabled via {Marker}", flagName, path);
        return Task.CompletedTask;
    }

    /// <summary>Starts the developer emulator if needed and launches Critical Ops inside it over
    /// adb — the direct "launch from the dev version" path, independent of the session pipeline.</summary>
    public async Task<string> LaunchGameAsync(string? installRootOverride = null, CancellationToken ct = default)
    {
        var root = ResolveInstallRoot(installRootOverride);
        if (root is null)
        {
            return "Google Play Games Developer Emulator was not found on this PC";
        }

        var adb = Path.Combine(root, "current", "emulator", "adb.exe");
        if (!File.Exists(adb))
        {
            return "adb.exe not found in the emulator install";
        }

        if (!IsEmulatorRunning(root))
        {
            var bootstrapper = Path.Combine(root, "Bootstrapper.exe");
            if (!File.Exists(bootstrapper))
            {
                return "Bootstrapper.exe not found in " + root;
            }

            _logger.LogInformation("Starting the developer emulator for a direct game launch");
            Process.Start(new ProcessStartInfo(bootstrapper) { UseShellExecute = true, WorkingDirectory = root });
        }

        var attached = false;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await RunAdbAsync(adb, $"connect {AdbEndpoint}", ct).ConfigureAwait(false);
            var state = await RunAdbAsync(adb, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
            if (state.Success && state.Output.Trim() == "device")
            {
                attached = true;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        if (!attached)
        {
            return "the emulator did not come up on adb within 30 seconds";
        }

        var monkey = await RunAdbAsync(adb, $"-s {AdbEndpoint} shell monkey -p {CriticalOpsPackage} 1", ct).ConfigureAwait(false);
        return monkey.Success
            ? "Critical Ops is starting in the developer emulator"
            : "adb could not start the game: " + monkey.Output;
    }

    /// <summary>Asks adb whether the emulator VM is attached and whether the game is running in it.</summary>
    public async Task<(bool DeviceAttached, bool GameRunning)> CheckGameStatusAsync(string? adbPath, CancellationToken ct = default)
    {
        if (adbPath is null || !File.Exists(adbPath))
        {
            return (false, false);
        }

        var state = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
        if (!state.Success || state.Output.Trim() != "device")
        {
            return (false, false);
        }

        var game = await RunAdbAsync(
            adbPath, $"-s {AdbEndpoint} shell pidof com.criticalforceentertainment.criticalops", ct).ConfigureAwait(false);
        return (true, game.Success && game.Output.Trim().Length > 0);
    }

    public void OpenInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Host-side refresh-rate read: the two Service.exe.config settings the community FPS
    /// unlock edits (Google clamps the developer emulator display to 60 Hz by default). Reading is
    /// unprivileged; writing goes through the elevated helper.</summary>
    public (int? RefreshRate, string? GpuRefreshRate, string? ConfigPath) ReadHostRefreshRate()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Play Games Developer Emulator", "current", "service", "Service.exe.config");
        if (!File.Exists(configPath))
        {
            return (null, null, null);
        }

        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(configPath);
            var settings = doc.SelectSingleNode(
                "configuration/applicationSettings/Google.Hpe.Service.Properties.EmulatorSettings");
            if (settings is null)
            {
                return (null, null, configPath);
            }

            var rate = settings.SelectSingleNode("setting[@name='EmulatorRefreshRate']/value")?.InnerText;
            var gpuRate = settings.SelectSingleNode("setting[@name='EmulatorGpuRefreshRate']/value")?.InnerText;
            return (int.TryParse(rate, out var parsed) ? parsed : null, gpuRate, configPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Service.exe.config refresh-rate settings");
            return (null, null, configPath);
        }
    }

    /// <summary>Applies the community FPS unlock over adb: peak/min refresh rate inside the guest.
    /// Returns a human-readable status.</summary>
    public async Task<string> ApplyGuestRefreshRateAsync(string? adbPath, int refreshRate, CancellationToken ct = default)
    {
        if (refreshRate is < 30 or > 240)
        {
            return "the refresh rate must be between 30 and 240";
        }
        if (adbPath is null || !File.Exists(adbPath))
        {
            return "adb.exe not found in the emulator install";
        }

        var state = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
        if (!state.Success || state.Output.Trim() != "device")
        {
            return "emulator not attached on adb — start it first (or launch via Optima)";
        }

        var peak = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings put system peak_refresh_rate {refreshRate}.0", ct).ConfigureAwait(false);
        var min = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings put system min_refresh_rate {refreshRate}.0", ct).ConfigureAwait(false);
        if (!peak.Success || !min.Success)
        {
            return "adb rejected the refresh-rate setting: " + (peak.Output is { Length: > 0 } o ? o : min.Output);
        }

        _logger.LogInformation("Guest peak/min refresh rate set to {Rate} Hz", refreshRate);
        return $"guest peak/min refresh rate set to {refreshRate} Hz (applies immediately)";
    }

    /// <summary>Reads the guest refresh-rate settings back over adb.</summary>
    public async Task<(int? Peak, int? Min, string Status)> ReadGuestRefreshRatesAsync(string? adbPath, CancellationToken ct = default)
    {
        if (adbPath is null || !File.Exists(adbPath))
        {
            return (null, null, "adb.exe not found");
        }

        var state = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
        if (!state.Success || state.Output.Trim() != "device")
        {
            return (null, null, "emulator not running");
        }

        var peak = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings get system peak_refresh_rate", ct).ConfigureAwait(false);
        var min = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings get system min_refresh_rate", ct).ConfigureAwait(false);
        int? Parse(string raw) => float.TryParse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var value) ? (int)value : null;
        return (Parse(peak.Output), Parse(min.Output),
            $"guest peak {peak.Output.Trim()} / min {min.Output.Trim()} Hz");
    }

    /// <summary>Pure helper: the adb commands that force (or clear) ANGLE as the GL driver for the
    /// game package. This is the scriptable rendering-backend lever inside the guest: the game's
    /// OpenGL ES goes through ANGLE onto Vulkan instead of the software GLES translator.</summary>
    public static IReadOnlyList<string> BuildGuestAngleCommands(bool forceAngle, string packageId = CriticalOpsPackage)
        => forceAngle
            ?
            [
                $"shell settings put global {AnglePkgSetting} {packageId}",
                $"shell settings put global {AngleValueSetting} {AngleDriverValue}",
            ]
            :
            [
                $"shell settings delete global {AnglePkgSetting}",
                $"shell settings delete global {AngleValueSetting}",
            ];

    /// <summary>Applies the guest ANGLE setting over adb. Returns a human-readable status; the
    /// emulator must be running (the setting lives inside the guest image).</summary>
    public async Task<string> ApplyGuestAngleAsync(string? adbPath, bool forceAngle, CancellationToken ct = default)
    {
        if (adbPath is null || !File.Exists(adbPath))
        {
            return "adb.exe not found in the emulator install";
        }

        var state = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
        if (!state.Success || state.Output.Trim() != "device")
        {
            return "emulator not attached on adb — start it first (or launch via Optima)";
        }

        foreach (var arguments in BuildGuestAngleCommands(forceAngle))
        {
            var result = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} {arguments}", ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("adb guest angle command failed: {Arguments} -> {Output}", arguments, result.Output);
                return "adb rejected the setting: " + result.Output;
            }
        }

        _logger.LogInformation("Guest ANGLE driver selection for the game set to {Mode}", forceAngle ? "forced" : "cleared");
        return forceAngle
            ? "applied — the game renders through ANGLE/Vulkan (takes effect when the game restarts)"
            : "cleared — the game uses the default guest GLES driver";
    }

    /// <summary>Reads the current guest GLES driver selection and whether the game is covered by it.</summary>
    public async Task<(bool Forced, string Status)> ReadGuestRendererStatusAsync(string? adbPath, CancellationToken ct = default)
    {
        if (adbPath is null || !File.Exists(adbPath))
        {
            return (false, "adb.exe not found");
        }

        var state = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} get-state", ct).ConfigureAwait(false);
        if (!state.Success || state.Output.Trim() != "device")
        {
            return (false, "emulator not running");
        }

        var pkgs = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings get global {AnglePkgSetting}", ct).ConfigureAwait(false);
        var values = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell settings get global {AngleValueSetting}", ct).ConfigureAwait(false);
        var egl = await RunAdbAsync(adbPath, $"-s {AdbEndpoint} shell getprop ro.hardware.egl", ct).ConfigureAwait(false);

        var forced = pkgs.Output.Trim().Contains(CriticalOpsPackage, StringComparison.OrdinalIgnoreCase)
            && values.Output.Trim().Contains(AngleDriverValue, StringComparison.OrdinalIgnoreCase);
        var activeDriver = egl.Output.Trim();
        return (forced, forced
            ? $"ANGLE forced for the game (active GLES driver: {activeDriver})"
            : $"default guest GLES driver (active: {(activeDriver.Length > 0 ? activeDriver : "unknown")})");
    }

    /// <summary>Pure helper: rewrites the semicolon-separated preference string so only the
    /// GpuPreference component changes; other components (e.g. SwapEffectUpgradeEnable) survive.</summary>
    public static string ApplyGpuPreference(string? current, GpuPreferenceKind kind)
    {
        var components = (current ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !c.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));

        if (kind != GpuPreferenceKind.Auto)
        {
            components = components.Append($"GpuPreference={(int)kind}");
        }

        var joined = string.Join(";", components);
        return joined.Length == 0 ? string.Empty : joined + ";";
    }

    public static (GpuPreferenceKind Kind, string? Raw) ParseGpuPreference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (GpuPreferenceKind.Auto, raw);
        }

        foreach (var component in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (component.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(component["GpuPreference=".Length..], out var value))
            {
                return (Enum.IsDefined(typeof(GpuPreferenceKind), value)
                    ? (GpuPreferenceKind)value
                    : GpuPreferenceKind.Auto, raw);
            }
        }

        return (GpuPreferenceKind.Auto, raw);
    }

    /// <summary>Pure helper: the marker file name Google's StartupPathFlagsModule writes.</summary>
    public static string MarkerFileName(string flagName, int version, string state, int series = 1)
        => $"{flagName}.v{version}.{state}.{series:D2}";

    private string? ResolveInstallRoot(string? installRootOverride)
    {
        if (!string.IsNullOrWhiteSpace(installRootOverride) && Directory.Exists(installRootOverride))
        {
            return installRootOverride;
        }

        var fallback = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Play Games Developer Emulator");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private string? ResolveCrosvmPath(string? installRootOverride)
    {
        var root = ResolveInstallRoot(installRootOverride);
        return root is null ? null : Path.Combine(root, "current", "emulator", "crosvm.exe");
    }

    private string? ReadInstalledVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKeyName + @"\version");
            return key?.GetValue(string.Empty) as string ?? key?.GetValue("Version") as string;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the developer emulator version from the registry");
            return null;
        }
    }

    private string? ReadGpuPreferenceValue(string crosvmPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesKeyName);
            return key?.GetValue(crosvmPath) as string;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the per-app GPU preference key");
            return null;
        }
    }

    private DevEmulatorFlagState ReadFlagState(string flagName)
    {
        try
        {
            var folder = StartupFlagsFolder();
            if (!Directory.Exists(folder))
            {
                return new DevEmulatorFlagState(flagName, false, "absent", 0);
            }

            // COMMITTED outranks TENTATIVE for the same flag; the highest version wins inside a state.
            string? best = null;
            foreach (var file in Directory.EnumerateFiles(folder, flagName + ".*"))
            {
                var name = Path.GetFileName(file);
                if (FlagMarkerRegex.IsMatch(name)
                    && (best is null || FlagMarkerRank(name) > FlagMarkerRank(best)))
                {
                    best = name;
                }
            }

            if (best is null)
            {
                return new DevEmulatorFlagState(flagName, false, "absent", 0);
            }

            var match = FlagMarkerRegex.Match(best);
            return new DevEmulatorFlagState(
                flagName,
                Present: true,
                match.Groups["state"].Value,
                int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read startup flag {Flag}", flagName);
            return new DevEmulatorFlagState(flagName, false, "absent", 0);
        }
    }

    private static readonly Regex FlagMarkerRegex =
        new(@"^(?<flag>.+)\.v(?<ver>\d+)\.(?<state>TENTATIVE|COMMITTED)\.(?<idx>\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int FlagMarkerRank(string markerName)
    {
        var match = FlagMarkerRegex.Match(markerName);
        var stateRank = match.Groups["state"].Value.Equals("COMMITTED", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 0;
        return stateRank + int.Parse(match.Groups["ver"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string StartupFlagsFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DevEmulatorDataFolder, "startup_flags");

    private bool IsEmulatorRunning(string? installRoot)
    {
        if (installRoot is null)
        {
            return false;
        }

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                string? imagePath = null;
                try
                {
                    imagePath = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied for elevated/system processes; skip them.
                }

                if (imagePath is not null
                    && imagePath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)
                    && imagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate processes to detect the developer emulator");
        }

        return false;
    }

    private static async Task<IReadOnlyList<Optima.Core.Models.GpuInfo>> EnumerateGpusAsync(CancellationToken ct)
    {
        var gpus = new List<Optima.Core.Models.GpuInfo>();
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
                foreach (var device in searcher.Get())
                {
                    var name = device["Name"]?.ToString() ?? "Unknown GPU";
                    gpus.Add(new Optima.Core.Models.GpuInfo
                    {
                        Name = name,
                        DriverVersion = device["DriverVersion"]?.ToString() ?? string.Empty,
                        VramBytes = device["AdapterRAM"] is uint ram ? ram : 0,
                        Vendor = ClassifyVendor(name),
                    });
                    device.Dispose();
                }
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // WMI can be slow or missing; the GPU list is informational only.
        }

        return gpus;
    }

    private static Optima.Core.Models.GpuVendor ClassifyVendor(string gpuName)
    {
        if (gpuName.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("geforce", StringComparison.OrdinalIgnoreCase))
        {
            return Optima.Core.Models.GpuVendor.Nvidia;
        }
        if (gpuName.Contains("amd", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("radeon", StringComparison.OrdinalIgnoreCase))
        {
            return Optima.Core.Models.GpuVendor.Amd;
        }
        if (gpuName.Contains("intel", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("arc", StringComparison.OrdinalIgnoreCase))
        {
            return Optima.Core.Models.GpuVendor.Intel;
        }
        return Optima.Core.Models.GpuVendor.Unknown;
    }

    private async Task<(bool Success, string Output)> RunAdbAsync(string adb, string arguments, CancellationToken ct)
    {
        try
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode == 0, string.Concat(output, error).Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "adb timed out");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "adb {Arguments} failed", arguments);
            return (false, string.Empty);
        }
    }
}
