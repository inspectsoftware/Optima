using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Optima.Core.Abstractions;
using Optima.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Optima.Platform.Windows.Elevation;

/// <summary>Client side of the elevated helper (§20).</summary>
public sealed class ElevationBrokerClient : IElevationBroker
{
    private readonly ILogger<ElevationBrokerClient> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<IpcResponse>> _pending = [];
    private readonly object _pendingLock = new();

    private NamedPipeServerStream? _pipe;
    private Process? _helperProcess;
    private CancellationTokenSource? _readLoopCts;
    private int _nextRequestId;

    public ElevationBrokerClient(ILogger<ElevationBrokerClient> logger)
    {
        _logger = logger;
    }

    public event EventHandler<IpcEvent>? EventReceived;

    public bool IsConnected => _pipe?.IsConnected == true;

    public ElevationStartFailure LastStartFailure { get; private set; }

    public bool CurrentProcessIsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// A cancel frees the caller at once; the attempt itself runs on to "connected" or "cleaned
    /// up", so a helper approved after the caller left is kept for whoever asks next.
    /// </summary>
    public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        => IsConnected ? Task.FromResult(true) : StartHelperAsync(ct).WaitAsync(ct);

    // Counts the prompts answered with No, so that a caller who waited behind one can tell.
    private int _declines;

    private async Task<bool> StartHelperAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            return true;
        }

        var declines = Volatile.Read(ref _declines);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return true;
            }
            // Queued behind a prompt that was answered with No: that answer stands for this
            // request as well. Asking again straight away is a second prompt for the same click.
            if (Volatile.Read(ref _declines) != declines)
            {
                return false;
            }

            CleanupConnection();

            var pipeName = "optima-elev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            // On a standard account the prompt is answered with another person's administrator
            // login, and the helper then runs as them: this user's SID alone would lock it out.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));

            _pipe = NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 65536, outBufferSize: 65536, security);

            var helperPath = Path.Combine(AppContext.BaseDirectory, "Optima.Watchdog.exe");
            if (!File.Exists(helperPath))
            {
                _logger.LogError("Elevated helper not found at {Path}", helperPath);
                LastStartFailure = ElevationStartFailure.HelperMissing;
                CleanupConnection();
                return false;
            }

            try
            {
                // On the pool: the call only returns once the prompt has been answered, and a caller
                // on the UI thread would be held for all of that time.
                _helperProcess = await Task.Run(() => Process.Start(new ProcessStartInfo(helperPath, $"--pipe {pipeName}")
                {
                    UseShellExecute = true,
                    Verb = "runas", // triggers UAC; helper's manifest also requires administrator
                    WindowStyle = ProcessWindowStyle.Hidden,
                })).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _logger.LogInformation("User declined the UAC prompt for the elevated helper");
                LastStartFailure = ElevationStartFailure.Declined;
                Interlocked.Increment(ref _declines);
                CleanupConnection();
                return false;
            }

            // Its own 30 seconds and not the caller's token. A caller can run out of time while the
            // prompt is still open, and the helper approved after that has to end up connected or
            // cleaned up, never running against a pipe nobody reads.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Elevated helper did not connect within 30 seconds");
                LastStartFailure = ElevationStartFailure.Timeout;
                CleanupConnection();
                return false;
            }

            _readLoopCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_pipe, _readLoopCts.Token), CancellationToken.None);
            _logger.LogInformation("Elevated helper connected");
            LastStartFailure = ElevationStartFailure.None;
            return true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
        {
            return new IpcResponse { Success = false, Error = "Elevated helper is not running.", RequestId = request.RequestId };
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var stamped = request with { RequestId = id };
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        try
        {
            await IpcFraming.WriteFrameAsync(pipe, stamped, ct).ConfigureAwait(false);
            await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            var response = await tcs.Task.ConfigureAwait(false);
            if (!response.Success && response.ErrorDetail.Length > 0)
            {
                // Callers get the one-line error to show; the helper's own stack goes to the log here,
                // once, instead of being lost at the process boundary.
                _logger.LogWarning("Elevated helper command {Command} threw: {Error}\n{Detail}",
                    request.Command, response.Error, response.ErrorDetail);
            }
            return response;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogError(ex, "IPC send failed, helper connection lost");
            return new IpcResponse { Success = false, Error = "Connection to the elevated helper was lost.", RequestId = id };
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await IpcFraming.ReadFrameAsync<IpcEnvelope>(pipe, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.Response is { } response)
                {
                    TaskCompletionSource<IpcResponse>? tcs;
                    lock (_pendingLock)
                    {
                        _pending.TryGetValue(response.RequestId, out tcs);
                    }
                    tcs?.TrySetResult(response);
                }
                else if (envelope.Event is { } evt)
                {
                    try
                    {
                        EventReceived?.Invoke(this, evt);
                    }
                    catch (Exception ex)
                    {
                        // A listener that throws must not take the loop, and every pending request, with it.
                        _logger.LogWarning(ex, "An IPC event listener failed");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "IPC read loop ended");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC read loop failed");
        }
        finally
        {
            // Closed here as well: a loop that ended on a bad frame leaves the pipe connected, and
            // every later request would be written to a helper nobody is listening to.
            pipe.Dispose();
            FailAllPending("The elevated helper disconnected.");
        }
    }

    private void FailAllPending(string reason)
    {
        List<TaskCompletionSource<IpcResponse>> waiting;
        lock (_pendingLock)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in waiting)
        {
            tcs.TrySetResult(new IpcResponse { Success = false, Error = reason });
        }
    }

    private void CleanupConnection()
    {
        _readLoopCts?.Cancel();
        _readLoopCts?.Dispose();
        _readLoopCts = null;
        _pipe?.Dispose();
        _pipe = null;
        _helperProcess?.Dispose();
        _helperProcess = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(new IpcRequest { Command = IpcCommand.Shutdown }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        FailAllPending("The application is shutting down.");
        CleanupConnection();
        _startGate.Dispose();
    }
}
