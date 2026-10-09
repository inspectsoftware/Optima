using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Optima.Core.Ipc;
using Optima.Core.Models;

namespace Optima.Watchdog;

/// <summary>Executes the whitelisted elevated commands (§20).</summary>
public sealed partial class CommandExecutor : IAsyncDisposable
{
    private const string AllowedVddPipe = "MTTVirtualDisplayPipe";
    private static readonly string[] AllowedVddCommands = ["RELOAD_DRIVER"];

    [GeneratedRegex(@"^[A-Za-z0-9\\&_.{}\-]+$")]
    private static partial Regex SafeInstanceIdPattern();

    private static readonly string[] AllowedWindowsFeatures = ["HypervisorPlatform", "VirtualMachinePlatform"];

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex ProgressPercentPattern();

    private readonly Func<IpcEvent, Task> _publishEvent;
    private EtwFrametimeCollector? _etw;
    private HardwareStreamer? _hardware;
    private StandbyListCleaner? _standbyCleaner;

    public CommandExecutor(Func<IpcEvent, Task> publishEvent)
    {
        _publishEvent = publishEvent;
    }

    public async Task<IpcResponse> ExecuteAsync(IpcRequest request, CancellationToken ct)
    {
        var ok = (Dictionary<string, string>? data) => new IpcResponse
        {
            Success = true,
            Data = data ?? [],
            RequestId = request.RequestId,
        };
        var fail = (string error) =>
        {
            HelperLog.Write($"{request.Command} FAILED: {error}");
            return new IpcResponse
            {
                Success = false,
                Error = error,
                RequestId = request.RequestId,
            };
        };

        HelperLog.Write($"{request.Command} received"
            + (request.Args.Count > 0
                ? " {" + string.Join(", ", request.Args.Select(a => $"{a.Key}={a.Value}")) + "}"
                : string.Empty));

        switch (request.Command)
        {
            case IpcCommand.Ping:
                return ok(new Dictionary<string, string> { ["pong"] = "1" });

            case IpcCommand.EnableDevice:
            case IpcCommand.DisableDevice:
            {
                if (!request.Args.TryGetValue("instanceId", out var instanceId) || instanceId.Length is 0 or > 200
                    || !SafeInstanceIdPattern().IsMatch(instanceId))
                {
                    return fail("Invalid device instance id.");
                }
                if (!await IsVirtualDisplayDeviceAsync(instanceId, ct))
                {
                    return fail("The device is not a recognized virtual display device.");
                }

                var verb = request.Command == IpcCommand.EnableDevice ? "/enable-device" : "/disable-device";
                var (exitCode, output) = await RunProcessAsync("pnputil.exe", $"{verb} \"{instanceId}\"", ct);
                return exitCode == 0 ? ok(null) : fail($"pnputil exited with {exitCode}: {Truncate(output)}");
            }

            case IpcCommand.WriteVddPipe:
            {
                if (!request.Args.TryGetValue("pipeName", out var pipeName) || pipeName != AllowedVddPipe)
                {
                    return fail("Pipe name not allowed.");
                }
                if (!request.Args.TryGetValue("command", out var command)
                    || !AllowedVddCommands.Contains(command, StringComparer.Ordinal))
                {
                    return fail("Pipe command not allowed.");
                }

                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                await pipe.ConnectAsync(TimeSpan.FromSeconds(3), ct);
                var bytes = Encoding.Unicode.GetBytes(command + "\0");
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
                return ok(null);
            }

            case IpcCommand.StartEtw:
            {
                var pidsText = request.Args.TryGetValue("pids", out var multi) ? multi
                    : request.Args.TryGetValue("pid", out var single) ? single
                    : null;
                if (pidsText is null)
                {
                    return fail("Invalid process id.");
                }
                var parts = pidsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var pids = new List<int>();
                foreach (var part in parts)
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                    {
                        return fail("Invalid process id.");
                    }
                    pids.Add(pid);
                }
                if (pids.Count is 0 or > 16)
                {
                    return fail("Between 1 and 16 process ids are required.");
                }

                var intervalMs = 1000;
                if (request.Args.TryGetValue("intervalMs", out var intervalText))
                {
                    if (!int.TryParse(intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out intervalMs)
                        || intervalMs is < 250 or > 2000)
                    {
                        return fail("intervalMs must be between 250 and 2000.");
                    }
                }

                if (_etw is not null)
                {
                    return fail("An ETW session is already running.");
                }

                var etw = new EtwFrametimeCollector(pids, intervalMs, _publishEvent);
                try
                {
                    etw.Start();
                }
                catch
                {
                    // Kept only once it runs: a session that failed to start would otherwise answer
                    // "already running" to every later start for the life of the helper.
                    etw.Dispose();
                    throw;
                }
                _etw = etw;
                return ok(null);
            }

            case IpcCommand.StopEtw:
            {
                if (_etw is null)
                {
                    return fail("No ETW session is running.");
                }
                var summary = _etw.Stop();
                _etw.Dispose();
                _etw = null;
                return ok(summary);
            }

            case IpcCommand.RunEtwProbe:
            {
                var durationSeconds = 10;
                if (request.Args.TryGetValue("durationSeconds", out var durationText))
                {
                    if (!int.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out durationSeconds)
                        || durationSeconds is < 3 or > 30)
                    {
                        return fail("durationSeconds must be between 3 and 30.");
                    }
                }
                if (_etw is not null)
                {
                    return fail("An ETW session is already running; stop it before probing.");
                }

                var counts = await EtwPresentProbe.RunAsync(TimeSpan.FromSeconds(durationSeconds), ct);
                var data = counts.ToDictionary(
                    kv => $"pid:{kv.Key.ToString(CultureInfo.InvariantCulture)}",
                    kv => kv.Value.ToString(CultureInfo.InvariantCulture));
                data["durationSeconds"] = durationSeconds.ToString(CultureInfo.InvariantCulture);
                return ok(data);
            }

            case IpcCommand.InstallDriver:
            {
                if (!request.Args.TryGetValue("infPath", out var infPath) || !IsAcceptableInfPath(infPath))
                {
                    return fail("Driver package path is not acceptable.");
                }
                if (!request.Args.TryGetValue("hardwareId", out var hardwareId)
                    || hardwareId.Length is 0 or > 200
                    || !SafeInstanceIdPattern().IsMatch(hardwareId))
                {
                    return fail("Invalid hardware id.");
                }
                if (!IsBundledHardwareId(hardwareId))
                {
                    return fail("That is not the driver Optima ships.");
                }

                var installed = await InstallDriverPackageAsync(infPath, hardwareId, ct);
                return installed.Success
                    ? ok(new Dictionary<string, string> { ["restartRequired"] = installed.RebootRequired ? "1" : "0" })
                    : fail(installed.Error);
            }

            case IpcCommand.UninstallDriver:
            {
                if (!request.Args.TryGetValue("hardwareId", out var hardwareId)
                    || hardwareId.Length is 0 or > 200
                    || !SafeInstanceIdPattern().IsMatch(hardwareId))
                {
                    return fail("Invalid hardware id.");
                }
                if (!IsBundledHardwareId(hardwareId))
                {
                    return fail("That is not the driver Optima ships.");
                }

                var (succeeded, removed, removeError) = DeviceInstaller.RemoveRootDevices(hardwareId);
                if (!succeeded)
                {
                    return fail(removeError);
                }

                var packagesDeleted = 0;
                if (request.Args.TryGetValue("infName", out var infName) && IsSafeInfName(infName))
                {
                    packagesDeleted = await DeleteStagedDriverPackagesAsync(infName, ct);
                }

                return ok(new Dictionary<string, string>
                {
                    ["removed"] = removed.ToString(CultureInfo.InvariantCulture),
                    ["packagesDeleted"] = packagesDeleted.ToString(CultureInfo.InvariantCulture),
                });
            }

            case IpcCommand.EnsureVddSettings:
            {
                if (!request.Args.TryGetValue("path", out var settingsPath) || !IsAcceptableSettingsPath(settingsPath))
                {
                    return fail("Settings path is not acceptable.");
                }
                if (!request.Args.TryGetValue("content", out var content) || content.Length is 0 or > 64 * 1024)
                {
                    return fail("Settings content is missing or too large.");
                }

                var directory = Path.GetDirectoryName(settingsPath);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }
                // Never clobber a configuration the user already has.
                if (File.Exists(settingsPath))
                {
                    return ok(new Dictionary<string, string> { ["created"] = "0" });
                }
                // Only ever the default file. What the caller sent is not written: this process is
                // the administrator, and the text would otherwise be the caller's to choose.
                await File.WriteAllTextAsync(settingsPath, Optima.Core.Configuration.VddSettingsDefaults.DefaultXml, ct);
                return ok(new Dictionary<string, string> { ["created"] = "1" });
            }

            case IpcCommand.ApplyTweakValues:
            {
                // The payload never carries key paths or value names: only a catalog tweak id
                // plus data for that tweak's own HKLM values. An arbitrary elevated registry
                // write through this pipe is therefore impossible; the worst a malicious
                // caller can do is toggle a documented tweak.
                if (!request.Args.TryGetValue("tweakId", out var tweakId)
                    || TweakCatalog.Find(tweakId) is not { } definition)
                {
                    return fail("Unknown tweak id.");
                }
                if (!request.Args.TryGetValue("values", out var valuesJson) || valuesJson.Length is 0 or > 8 * 1024)
                {
                    return fail("Missing or oversized values payload.");
                }

                Dictionary<string, string?>? targets;
                try
                {
                    targets = JsonSerializer.Deserialize<Dictionary<string, string?>>(valuesJson);
                }
                catch (JsonException)
                {
                    return fail("Values payload is not valid JSON.");
                }
                if (targets is null || targets.Count == 0)
                {
                    return fail("Values payload is empty.");
                }

                var writes = new List<(TweakValue Value, string? Data)>();
                foreach (var (key, data) in targets)
                {
                    var value = definition.Values.FirstOrDefault(v =>
                        v.Hive == TweakHive.LocalMachine && string.Equals(TweakCatalog.ValueKey(v), key, StringComparison.Ordinal));
                    if (value is null)
                    {
                        return fail($"Value '{key}' is not an HKLM value of tweak '{tweakId}'.");
                    }
                    if (data is { Length: > 256 })
                    {
                        return fail("Value data is too long.");
                    }
                    if (data is not null && value.Kind == TweakValueKind.Dword
                        && !uint.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        return fail($"'{data}' is not a valid DWORD for '{key}'.");
                    }
                    writes.Add((value, data));
                }

                foreach (var (value, data) in writes)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(value.KeyPath);
                    if (data is null)
                    {
                        key.DeleteValue(value.ValueName, throwOnMissingValue: false);
                    }
                    else if (value.Kind == TweakValueKind.Dword)
                    {
                        key.SetValue(value.ValueName,
                            unchecked((int)uint.Parse(data, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                            Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    else
                    {
                        key.SetValue(value.ValueName, data, Microsoft.Win32.RegistryValueKind.String);
                    }
                    HelperLog.Write($"Tweak '{tweakId}': {TweakCatalog.ValueKey(value)} = {data ?? "<deleted>"}");
                }
                return ok(null);
            }

            case IpcCommand.ReadBcdVirtualization:
            {
                var (exitCode, output) = await RunProcessAsync("bcdedit.exe", "/enum {current}", ct);
                if (exitCode != 0)
                {
                    return fail($"bcdedit exited with {exitCode}.");
                }
                var match = Regex.Match(output, @"hypervisorlaunchtype\s+(\S+)", RegexOptions.IgnoreCase);
                return ok(new Dictionary<string, string>
                {
                    ["hypervisorLaunchType"] = match.Success ? match.Groups[1].Value : "unknown",
                });
            }

            case IpcCommand.StartHardwareStream:
            {
                if (_hardware is not null)
                {
                    return ok(null);
                }
                try
                {
                    _hardware = new HardwareStreamer(_publishEvent);
                    return ok(null);
                }
                catch (Exception ex)
                {
                    return fail("Hardware monitoring could not start: " + ex.Message);
                }
            }

            case IpcCommand.StopHardwareStream:
                _hardware?.Dispose();
                _hardware = null;
                return ok(null);

            case IpcCommand.StartStandbyCleaner:
            {
                if (!TryReadInt(request.Args, "freeBelowMb", out var freeBelowMb)
                    || !TryReadInt(request.Args, "standbyAboveMb", out var standbyAboveMb)
                    || !TryReadInt(request.Args, "intervalMs", out var cleanerIntervalMs)
                    || !Optima.Core.Boost.StandbyCleanerPolicy.IsValid(freeBelowMb, standbyAboveMb, cleanerIntervalMs))
                {
                    return fail("The memory cleaner thresholds are missing or out of range.");
                }
                try
                {
                    if (_standbyCleaner is not null)
                    {
                        _standbyCleaner.Configure(freeBelowMb, standbyAboveMb, cleanerIntervalMs);
                    }
                    else
                    {
                        _standbyCleaner = new StandbyListCleaner(_publishEvent, freeBelowMb, standbyAboveMb, cleanerIntervalMs);
                    }
                    return ok(null);
                }
                catch (Exception ex)
                {
                    return fail("The memory cleaner could not start: " + ex.Message);
                }
            }

            case IpcCommand.StopStandbyCleaner:
                _standbyCleaner?.Dispose();
                _standbyCleaner = null;
                return ok(null);

            case IpcCommand.PurgeStandbyList:
                try
                {
                    return ok(StandbyListCleaner.PurgeOnce());
                }
                catch (Exception ex)
                {
                    return fail("The standby list could not be purged: " + ex.Message);
                }

            case IpcCommand.LaunchShield:
            {
                // Starts the protection module with this helper's rights. The only thing a caller
                // chooses is the mode. The file is the one beside this helper, and only when it is
                // the file this build of Optima was made with: its SHA-256 was written into this
                // assembly at build time. It is held open, without write sharing, from the hash to
                // the start, so the file that runs is the file that was checked.
                if (!request.Args.TryGetValue("mode", out var mode) || mode is not ("play" or "watch"))
                {
                    return fail("Invalid mode.");
                }
                var expected = System.Reflection.CustomAttributeExtensions
                    .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(CommandExecutor).Assembly)
                    .FirstOrDefault(a => a.Key == "ShieldSha256")?.Value;
                if (string.IsNullOrEmpty(expected))
                {
                    return fail("This build of Optima has no protection module.");
                }

                var path = Path.Combine(AppContext.BaseDirectory, "Optima.Shield.exe");
                try
                {
                    await using var exe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(exe, ct));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return fail("Optima Shield is not the file this version of Optima was built with.");
                    }

                    using var started = Process.Start(new ProcessStartInfo(path, "mode=" + mode)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = AppContext.BaseDirectory,
                    });
                    return started is null ? fail("Optima Shield did not start.") : ok(null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    return fail("Optima Shield could not be started: " + ex.Message);
                }
            }

            case IpcCommand.EnableWindowsFeature:
            {
                if (!request.Args.TryGetValue("feature", out var feature)
                    || !AllowedWindowsFeatures.Contains(feature, StringComparer.OrdinalIgnoreCase))
                {
                    return fail("The feature name is not on the allowed list.");
                }
                var (exitCode, output) = await RunProcessAsync(
                    "dism.exe", $"/online /enable-feature /featurename:{feature} /norestart", ct,
                    timeout: TimeSpan.FromMinutes(8),
                    onOutputLine: async line =>
                    {
                        var match = ProgressPercentPattern().Match(line);
                        if (match.Success)
                        {
                            var percent = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            await _publishEvent(new IpcEvent
                            {
                                Kind = "feature-progress",
                                Data =
                                {
                                    ["feature"] = feature,
                                    ["percent"] = percent.ToString("0.#", CultureInfo.InvariantCulture),
                                },
                            });
                        }
                    });
                if (exitCode is not (0 or 3010))
                {
                    return fail($"dism exited with {exitCode}: {Truncate(output)}");
                }
                return ok(new Dictionary<string, string>
                {
                    ["restartRequired"] = exitCode == 3010
                        || output.Contains("restart", StringComparison.OrdinalIgnoreCase) ? "1" : "0",
                });
            }

            case IpcCommand.Shutdown:
                return ok(null);

            default:
                return fail($"Command {request.Command} is not supported.");
        }
    }

    /// <summary>
    /// Stages a driver package and gives it a device node. Both callers go through here: the
    /// InstallDriver command from the running app, and the installer's one-shot mode, which runs
    /// the elevated helper directly. Re-running it is safe, because the device node is only
    /// created when no present device carries the hardware id yet.
    /// </summary>
    internal static async Task<(bool Success, bool RebootRequired, bool AlreadyPresent, string Error)> InstallDriverPackageAsync(
        string infPath, string hardwareId, CancellationToken ct)
    {
        var (stageCode, stageOutput) = await RunProcessAsync("pnputil.exe", $"/add-driver \"{infPath}\" /install", ct);
        HelperLog.Write($"pnputil /add-driver exit={stageCode}: {Truncate(stageOutput)}");
        // 259 is pnputil saying the package is already staged and up to date, which is what every
        // install over an existing one finds. It is the same footing as 0 for what follows.
        // 3010 is success with a restart owed, which is what replacing a driver that is in use gets.
        if (stageCode is not (0 or 259 or 3010))
        {
            return (false, false, false, $"pnputil could not stage the driver package (exit {stageCode}). {Truncate(stageOutput)}");
        }
        var stageNeedsRestart = stageCode == 3010;

        if (DeviceInstaller.RootDeviceExists(hardwareId))
        {
            HelperLog.Write($"A present device already carries {hardwareId}; the package was updated in place");
            return (true, stageNeedsRestart, true, string.Empty);
        }

        var (created, reboot, createError) = DeviceInstaller.CreateRootDevice(hardwareId, infPath);
        HelperLog.Write($"CreateRootDevice created={created} reboot={reboot} error={createError}");

        // A new device node comes up enabled, which puts a virtual monitor on the desktop the moment
        // the driver is installed, with no session asking for one. Installed means available, not
        // on: a session (or the Display page) enables the device when it wants the display.
        if (created && SafeInstanceIdPattern().IsMatch(hardwareId))
        {
            var (disableCode, disableOutput) = await RunProcessAsync("pnputil.exe", $"/disable-device /deviceid \"{hardwareId}\"", ct);
            HelperLog.Write($"pnputil /disable-device after create exit={disableCode}: {Truncate(disableOutput)}");
        }
        return created
            ? (true, reboot || stageNeedsRestart, false, string.Empty)
            : (false, false, false, createError);
    }

    private static bool IsAcceptableInfPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400)
        {
            return false;
        }
        if (!path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string full, root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
    }

    /// <summary>
    /// The install and remove commands act on one device only, the bundled virtual display. A
    /// hardware id taken on trust would let a caller remove any display adapter, the real one included.
    /// </summary>
    private static bool IsBundledHardwareId(string hardwareId)
        => Optima.Core.Detection.BundledDriverPackage.Find(Path.Combine(AppContext.BaseDirectory, "drivers")) is { } package
            && string.Equals(package.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptableSettingsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400)
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFileName(Path.GetFullPath(path)), "vdd_settings.xml", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Task<bool> IsVirtualDisplayDeviceAsync(string instanceId, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
                foreach (var entity in searcher.Get())
                {
                    var id = entity["PNPDeviceID"]?.ToString();
                    var name = entity["Name"]?.ToString() ?? string.Empty;
                    if (string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)
                        && name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }, ct);

    private static bool IsSafeInfName(string name)
        => name.Length is > 4 and <= 100 && SafeInfNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeInfNamePattern();

    private static async Task<int> DeleteStagedDriverPackagesAsync(string infName, CancellationToken ct)
    {
        var (enumCode, enumOutput) = await RunProcessAsync("pnputil.exe", "/enum-drivers", ct);
        if (enumCode != 0)
        {
            HelperLog.Write($"pnputil /enum-drivers exit={enumCode}: {Truncate(enumOutput)}");
            return 0;
        }

        var deleted = 0;
        foreach (var published in FindPublishedDriverNames(enumOutput, infName))
        {
            var (delCode, delOutput) = await RunProcessAsync("pnputil.exe", $"/delete-driver {published} /uninstall /force", ct);
            HelperLog.Write($"pnputil /delete-driver {published} exit={delCode}: {Truncate(delOutput)}");
            if (delCode == 0)
            {
                deleted++;
            }
        }
        return deleted;
    }

    internal static IReadOnlyList<string> FindPublishedDriverNames(string pnputilOutput, string originalInfName)
    {
        var results = new List<string>();
        // A published name is never an original name: asked for "oem12.inf", the search would
        // match that package's own entry and delete whatever driver it is.
        if (Regex.IsMatch(originalInfName, @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
        {
            return results;
        }
        // The whole value of a line, not a part of one ("vdd.inf" is not "myvdd.inf").
        var wholeValue = new Regex($@":\s*{Regex.Escape(originalInfName)}\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var block in pnputilOutput.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!wholeValue.IsMatch(block))
            {
                continue;
            }
            var match = Regex.Match(block, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                results.Add(match.Value.ToLowerInvariant());
            }
        }
        return results.Distinct().ToList();
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan? timeout = null,
        Func<string, Task>? onOutputLine = null)
    {
        // Always the copy in System32. A bare name is looked up in the helper's own folder first,
        // and that folder is the user's: a file dropped there under a tool's name would otherwise
        // be started with this process's administrator rights.
        var toolPath = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Environment.SystemDirectory, fileName);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(toolPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        // A hung tool (dism against a stuck servicing stack, for example) must not wedge the elevated
        // helper forever: after the timeout the process tree is killed and a synthetic failure returns.
        // Every tool gets a limit, because the helper answers one request at a time and would not
        // even see the app closing behind a tool that never returns.
        var limit = timeout ?? DefaultToolTimeout;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(limit);
        try
        {
            // Both streams are read at once: a tool that fills the error pipe while only its
            // output is being read never exits.
            var errorTask = process.StandardError.ReadToEndAsync(lifetime.Token);
            // Tools like dism redraw progress with \r on one line, so ReadLine/ReadToEnd never see the
            // intermediate updates. Split on both \r and \n as characters arrive to stream them.
            var output = onOutputLine is null
                ? await process.StandardOutput.ReadToEndAsync(lifetime.Token)
                : await StreamLinesAsync(process.StandardOutput, onOutputLine, lifetime.Token);
            var error = await errorTask;
            await process.WaitForExitAsync(lifetime.Token);
            return (process.ExitCode, output + error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone or access denied; either way there is nothing more to do
            }
            ct.ThrowIfCancellationRequested();
            return (-1, $"{fileName} was killed after {limit.TotalMinutes:F0} min with no result.");
        }
    }

    private static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromMinutes(5);

    private static async Task<string> StreamLinesAsync(
        StreamReader reader, Func<string, Task> onLine, CancellationToken ct)
    {
        var buffer = new char[512];
        var line = new StringBuilder();
        var all = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                break;
            }
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        var text = line.ToString();
                        all.AppendLine(text);
                        await onLine(text);
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(ch);
                }
            }
        }
        if (line.Length > 0)
        {
            var text = line.ToString();
            all.Append(text);
            await onLine(text);
        }
        return all.ToString();
    }

    private static string Truncate(string text)
        => text.Length <= 400 ? text.Trim() : text[..400].Trim() + "…";

    public ValueTask DisposeAsync()
    {
        _etw?.Dispose();
        _etw = null;
        _hardware?.Dispose();
        _hardware = null;
        _standbyCleaner?.Dispose();
        _standbyCleaner = null;
        return ValueTask.CompletedTask;
    }

    private static bool TryReadInt(Dictionary<string, string> args, string name, out int value)
    {
        value = 0;
        return args.TryGetValue(name, out var text)
            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
