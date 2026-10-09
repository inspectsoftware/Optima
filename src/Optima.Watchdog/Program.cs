using System.IO.Pipes;
using Optima.Core.Ipc;
using Optima.Watchdog;

// Optima.Watchdog is the only elevated part of the application (§20).
// It connects back to the named pipe hosted by the non-elevated UI, then executes a small,
// closed set of validated commands. It never shows UI and exits when the pipe closes.

// One-shot installer mode: the setup launches the helper elevated to install the bundled virtual
// display driver, and this process exits instead of serving the pipe.
if (args.Length > 0 && args[0] == InstallDriverCommand.SwitchName)
{
    return await InstallDriverCommand.RunAsync(args, CancellationToken.None);
}

var pipeName = ParsePipeName(args);
if (pipeName is null)
{
    Console.Error.WriteLine("Usage: Optima.Watchdog --pipe <name>");
    return 2;
}

if (!pipeName.StartsWith("optima-elev-", StringComparison.Ordinal) || pipeName.Length > 64)
{
    Console.Error.WriteLine("Refusing unexpected pipe name.");
    return 3;
}

HelperLog.Write($"helper starting, pipe={pipeName}, elevated={System.Security.Principal.WindowsIdentity.GetCurrent().Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)}");

await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    await pipe.ConnectAsync(10_000);
}
catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or IOException)
{
    HelperLog.Write($"could not connect to the pipe: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine("Could not connect to the bootstrapper pipe.");
    return 4;
}

using var shutdownCts = new CancellationTokenSource();
var writeLock = new SemaphoreSlim(1, 1);
await using var executor = new CommandExecutor(async evt =>
{
    await writeLock.WaitAsync();
    try
    {
        await IpcFraming.WriteFrameAsync(pipe, new IpcEnvelope { Event = evt });
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
    }
    finally
    {
        writeLock.Release();
    }
});

try
{
    while (!shutdownCts.IsCancellationRequested)
    {
        var request = await IpcFraming.ReadFrameAsync<IpcRequest>(pipe, shutdownCts.Token);
        if (request is null)
        {
            break;
        }

        IpcResponse response;
        try
        {
            response = await executor.ExecuteAsync(request, shutdownCts.Token);
        }
        catch (Exception ex)
        {
            var detail = Optima.Core.Health.ExceptionDetail.Capture(ex);
            HelperLog.Write($"{request.Command} failed: {detail.Summary}");
            response = new IpcResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorDetail = detail.FullText,
                RequestId = request.RequestId,
            };
        }

        await writeLock.WaitAsync();
        try
        {
            try
            {
                await IpcFraming.WriteFrameAsync(pipe, new IpcEnvelope { Response = response });
            }
            catch (InvalidOperationException ex)
            {
                // An answer over the frame limit. The caller still has to hear something, or it
                // waits on this request until the helper is gone.
                HelperLog.Write($"{request.Command} answer not sent: {ex.Message}");
                await IpcFraming.WriteFrameAsync(pipe, new IpcEnvelope
                {
                    Response = new IpcResponse { Success = false, Error = "The helper's answer was too large to send.", RequestId = request.RequestId },
                });
            }
        }
        finally
        {
            writeLock.Release();
        }

        if (request.Command == IpcCommand.Shutdown)
        {
            shutdownCts.Cancel();
        }
    }
}
catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
{
}
catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
{
    // A frame this build cannot read. Leaving through here still runs the disposal below, which
    // is what stops the trace session and the hardware reader.
    HelperLog.Write($"unreadable request, helper exiting: {ex.Message}");
}

return 0;

static string? ParsePipeName(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--pipe")
        {
            return args[i + 1];
        }
    }
    return null;
}
