using Optima.Core.Ipc;

namespace Optima.Core.Abstractions;

/// <summary>Why the elevated helper is not running after the last attempt to start it.</summary>
public enum ElevationStartFailure
{
    /// <summary>It started, or nobody has tried yet.</summary>
    None,

    /// <summary>The administrator prompt was answered with No.</summary>
    Declined,
    HelperMissing,
    Timeout,
}

/// <summary>Client side of the elevated helper (§20).</summary>
public interface IElevationBroker : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// How the last start went. A declined prompt is the one that matters: it is the player saying
    /// no, and nothing that runs unasked may put the question to them again.
    /// </summary>
    ElevationStartFailure LastStartFailure => ElevationStartFailure.None;

    bool CurrentProcessIsElevated { get; }

    Task<bool> EnsureStartedAsync(CancellationToken ct = default);

    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default);

    event EventHandler<IpcEvent>? EventReceived;
}
