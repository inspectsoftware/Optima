using System.Text.Json;
using Optima.Core.Ipc;
using Optima.Core.Linking;
using Xunit;

namespace Optima.Tests.Linking;

public sealed class LinkCodeTests
{
    [Theory]
    [InlineData("OPT-7F3KQ", "OPT-7F3KQ")]
    [InlineData("opt 7f3kq", "OPT-7F3KQ")]
    [InlineData("7F3KQ", "OPT-7F3KQ")]
    // The bot can mint longer codes; an app that refused them could not link once it does.
    [InlineData("OPT-7F3KQ2AB", "OPT-7F3KQ2AB")]
    [InlineData("OPT-7F3K", null)]
    [InlineData("OPT-7F3KQ2ABC", null)]
    [InlineData("OPT-7F3K0", null)]
    [InlineData("", null)]
    public void ACodeIsFiveToEightCharactersOfTheAlphabet(string typed, string? expected)
        => Assert.Equal(expected, LinkCode.Normalize(typed));

    [Fact]
    public void TheHelperIsAskedToStartTheModuleByName()
    {
        var request = new IpcRequest { Command = IpcCommand.LaunchShield, Args = { ["mode"] = "play" } };

        var json = JsonSerializer.Serialize(request, IpcJson.Options);

        Assert.Contains("\"command\":\"LaunchShield\"", json);
        Assert.Equal(IpcCommand.LaunchShield, JsonSerializer.Deserialize<IpcRequest>(json, IpcJson.Options)!.Command);
    }
}
