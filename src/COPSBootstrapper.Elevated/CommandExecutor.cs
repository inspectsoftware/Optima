using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using COPSBootstrapper.Core.Ipc;

namespace COPSBootstrapper.Elevated;

/// <summary>
/// Executes the whitelisted elevated commands (§20). Every argument is validated before use:
/// device ids must belong to an actual virtual display device, pipe writes are limited to the
/// known driver pipe and known command strings, and nothing else is accepted.
/// </summary>
public sealed partial class CommandExecutor : IAsyncDisposable
{
    private const string AllowedVddPipe = "MTTVirtualDisplayPipe";
    private static readonly string[] AllowedVddCommands = ["RELOAD_DRIVER"];

    [GeneratedRegex(@"^[A-Za-z0-9\\&_.{}\-]+$")]
    private static partial Regex SafeInstanceIdPattern();

    private readonly Func<IpcEvent, Task> _publishEvent;
    private EtwFrametimeCollector? _etw;

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
        var fail = (string error) => new IpcResponse
        {
            Success = false,
            Error = error,
            RequestId = request.RequestId,
        };

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
                if (!request.Args.TryGetValue("pid", out var pidText)
                    || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    || pid <= 0)
                {
                    return fail("Invalid process id.");
                }
                if (_etw is not null)
                {
                    return fail("An ETW session is already running.");
                }

                _etw = new EtwFrametimeCollector(pid, _publishEvent);
                _etw.Start();
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

            case IpcCommand.Shutdown:
                return ok(null);

            default:
                return fail($"Command {request.Command} is not supported.");
        }
    }

    /// <summary>Confirms via WMI that the instance id belongs to a display-class virtual display device.</summary>
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
                // Treat lookup failure as "not verified".
            }
            return false;
        }, ct);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output + error);
    }

    private static string Truncate(string text)
        => text.Length <= 400 ? text.Trim() : text[..400].Trim() + "…";

    public ValueTask DisposeAsync()
    {
        _etw?.Dispose();
        _etw = null;
        return ValueTask.CompletedTask;
    }
}
