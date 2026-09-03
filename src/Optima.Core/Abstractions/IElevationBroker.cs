using Optima.Core.Ipc;

namespace Optima.Core.Abstractions;

/// <summary>Client side of the elevated helper (§20).</summary>
public interface IElevationBroker : IAsyncDisposable
{
    bool IsConnected { get; }

    bool CurrentProcessIsElevated { get; }

    Task<bool> EnsureStartedAsync(CancellationToken ct = default);

    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default);

    event EventHandler<IpcEvent>? EventReceived;
}
