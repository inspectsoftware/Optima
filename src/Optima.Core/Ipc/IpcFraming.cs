using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Optima.Core.Ipc;

/// <summary>Length-prefixed JSON framing shared by both pipe ends.</summary>
public static class IpcFraming
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async Task WriteFrameAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (json.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"IPC frame too large ({json.Length} bytes).");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct = default) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length {length}.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, payload, ct).ConfigureAwait(false))
        {
            throw new EndOfStreamException("IPC stream ended mid-frame.");
        }

        return JsonSerializer.Deserialize<T>(payload, IpcJson.Options)
            ?? throw new InvalidDataException("IPC frame deserialized to null.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("IPC stream ended mid-frame.");
            }
            read += n;
        }
        return true;
    }
}
