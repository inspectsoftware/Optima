using System.Buffers;
using System.Buffers.Binary;
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

        // Header and payload leave in one rented buffer: one write per frame instead of three, and
        // nothing for either end to collect afterwards. The pool hands out larger arrays, so the
        // write is bounded to the frame.
        var frame = ArrayPool<byte>.Shared.Rent(json.Length + 4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
            json.CopyTo(frame.AsSpan(4));
            await stream.WriteAsync(frame.AsMemory(0, json.Length + 4), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
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

        var payload = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!await ReadExactlyOrEofAsync(stream, payload.AsMemory(0, length), ct).ConfigureAwait(false))
            {
                throw new EndOfStreamException("IPC stream ended mid-frame.");
            }

            // The rented buffer may be larger than the frame; only the frame is deserialized.
            return JsonSerializer.Deserialize<T>(payload.AsSpan(0, length), IpcJson.Options)
                ?? throw new InvalidDataException("IPC frame deserialized to null.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("IPC stream ended mid-frame.");
            }
            read += n;
        }
        return true;
    }
}
