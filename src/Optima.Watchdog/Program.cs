using System.IO.Pipes;
using Optima.Core.Ipc;
using Optima.Watchdog;

// Optima.Watchdog is the only elevated part of the application (§20).
// It connects back to the named pipe hosted by the non-elevated UI, then executes a small,
// closed set of validated commands. It never shows UI and exits when the pipe closes.

var pipeName = ParsePipeName(args);
if (pipeName is null)
{
    Console.Error.WriteLine("Usage: Optima.Watchdog --pipe <name>");
    return 2;
}

// Only accept locally generated pipe names to avoid being pointed at arbitrary pipes.
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
catch (TimeoutException)
{
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
        // Pipe gone, main loop will notice and exit.
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
            break; // clean EOF, bootstrapper exited
        }

        IpcResponse response;
        try
        {
            response = await executor.ExecuteAsync(request, shutdownCts.Token);
        }
        catch (Exception ex)
        {
            response = new IpcResponse
            {
                Success = false,
                Error = ex.Message,
                RequestId = request.RequestId,
            };
        }

        await writeLock.WaitAsync();
        try
        {
            await IpcFraming.WriteFrameAsync(pipe, new IpcEnvelope { Response = response });
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
    // Connection lost or shutting down, fall through to cleanup.
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
