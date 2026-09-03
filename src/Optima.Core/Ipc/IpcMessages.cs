using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optima.Core.Ipc;

/// <summary>Commands the elevated helper accepts (§20).</summary>
public enum IpcCommand
{
    Ping,
    EnableDevice,
    DisableDevice,
    WriteVddPipe,
    StartEtw,
    StopEtw,
    RunEtwProbe,
    ReadBcdVirtualization,
    InstallDriver,
    UninstallDriver,
    EnsureVddSettings,
    ApplyTweakValues,
    StartHardwareStream,
    StopHardwareStream,
    EnableWindowsFeature,
    Shutdown,
}

public sealed record IpcRequest
{
    public required IpcCommand Command { get; init; }
    public Dictionary<string, string> Args { get; init; } = [];
    public int RequestId { get; init; }
}

public sealed record IpcResponse
{
    public required bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public Dictionary<string, string> Data { get; init; } = [];
    public int RequestId { get; init; }
}

/// <summary>Unsolicited event pushed from the helper (e.g. one ETW frametime sample per second).</summary>
public sealed record IpcEvent
{
    public required string Kind { get; init; }
    public Dictionary<string, string> Data { get; init; } = [];
}

/// <summary>Envelope so responses and events can share one pipe.</summary>
public sealed record IpcEnvelope
{
    public IpcResponse? Response { get; init; }
    public IpcEvent? Event { get; init; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
