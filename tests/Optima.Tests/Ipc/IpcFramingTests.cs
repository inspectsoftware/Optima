using Optima.Core.Ipc;
using Xunit;

namespace Optima.Tests.Ipc;

public class IpcFramingTests
{
    [Fact]
    public async Task RoundTrip_RequestSurvivesFraming()
    {
        using var stream = new MemoryStream();
        var request = new IpcRequest
        {
            Command = IpcCommand.EnableDevice,
            Args = { ["instanceId"] = @"ROOT\DISPLAY\0000" },
            RequestId = 7,
        };

        await IpcFraming.WriteFrameAsync(stream, request);
        stream.Position = 0;
        var read = await IpcFraming.ReadFrameAsync<IpcRequest>(stream);

        Assert.NotNull(read);
        Assert.Equal(IpcCommand.EnableDevice, read.Command);
        Assert.Equal(@"ROOT\DISPLAY\0000", read.Args["instanceId"]);
        Assert.Equal(7, read.RequestId);
    }

    [Fact]
    public async Task RoundTrip_EnvelopeWithEvent()
    {
        using var stream = new MemoryStream();
        var envelope = new IpcEnvelope
        {
            Event = new IpcEvent { Kind = "etwSample", Data = { ["fps"] = "240.0" } },
        };

        await IpcFraming.WriteFrameAsync(stream, envelope);
        stream.Position = 0;
        var read = await IpcFraming.ReadFrameAsync<IpcEnvelope>(stream);

        Assert.NotNull(read?.Event);
        Assert.Null(read.Response);
        Assert.Equal("240.0", read.Event.Data["fps"]);
    }

    [Fact]
    public async Task ReadFrame_CleanEof_ReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await IpcFraming.ReadFrameAsync<IpcRequest>(stream));
    }

    [Fact]
    public async Task ReadFrame_TruncatedFrame_Throws()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, new IpcRequest { Command = IpcCommand.Ping });
        var bytes = stream.ToArray();

        using var truncated = new MemoryStream(bytes[..^3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => IpcFraming.ReadFrameAsync<IpcRequest>(truncated));
    }

    [Fact]
    public async Task ReadFrame_HostileLengthHeader_IsRejected()
    {
        // Length prefix claims 512 MB, must be rejected before any allocation.
        using var stream = new MemoryStream([0x00, 0x00, 0x00, 0x20, 0x01, 0x02]);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcFraming.ReadFrameAsync<IpcRequest>(stream));
    }

    [Fact]
    public async Task MultipleFrames_ReadSequentially()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, new IpcRequest { Command = IpcCommand.Ping, RequestId = 1 });
        await IpcFraming.WriteFrameAsync(stream, new IpcRequest { Command = IpcCommand.StopEtw, RequestId = 2 });
        stream.Position = 0;

        var first = await IpcFraming.ReadFrameAsync<IpcRequest>(stream);
        var second = await IpcFraming.ReadFrameAsync<IpcRequest>(stream);

        Assert.Equal(IpcCommand.Ping, first!.Command);
        Assert.Equal(IpcCommand.StopEtw, second!.Command);
    }
}
