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

    public bool CurrentProcessIsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return true;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return true;
            }

            CleanupConnection();

            var pipeName = "optima-elev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent())
            {
                security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            _pipe = NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 65536, outBufferSize: 65536, security);

            var helperPath = Path.Combine(AppContext.BaseDirectory, "Optima.Watchdog.exe");
            if (!File.Exists(helperPath))
            {
                _logger.LogError("Elevated helper not found at {Path}", helperPath);
                CleanupConnection();
                return false;
            }

            try
            {
                _helperProcess = Process.Start(new ProcessStartInfo(helperPath, $"--pipe {pipeName}")
                {
                    UseShellExecute = true,
                    Verb = "runas", // triggers UAC; helper's manifest also requires administrator
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _logger.LogInformation("User declined the UAC prompt for the elevated helper");
                CleanupConnection();
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogError("Elevated helper did not connect within 30 seconds");
                CleanupConnection();
                return false;
            }

            _readLoopCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_pipe, _readLoopCts.Token), CancellationToken.None);
            _logger.LogInformation("Elevated helper connected");
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
            return await tcs.Task.ConfigureAwait(false);
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
                    EventReceived?.Invoke(this, evt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "IPC read loop ended");
        }

        FailAllPending("The elevated helper disconnected.");
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
