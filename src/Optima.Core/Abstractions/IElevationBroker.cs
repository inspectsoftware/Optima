using Optima.Core.Ipc;

namespace Optima.Core.Abstractions;

/// <summary>
/// Client side of the elevated helper (§20). Starts Optima.Watchdog.exe on demand
/// (UAC prompt) and exchanges whitelisted commands over a private named pipe.
/// </summary>
public interface IElevationBroker : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>True when the current process itself already runs elevated.</summary>
    bool CurrentProcessIsElevated { get; }

    /// <summary>Starts (or reuses) the helper. Returns false if the user declined the UAC prompt.</summary>
    Task<bool> EnsureStartedAsync(CancellationToken ct = default);

    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default);

    /// <summary>Raised for streamed events (e.g. ETW frametime samples) pushed by the helper.</summary>
    event EventHandler<IpcEvent>? EventReceived;
}
