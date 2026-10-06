using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Linking;
using Optima.Tests.Stats;
using Xunit;

namespace Optima.Tests.Linking;

/// <summary>
/// The app's half of the link handshake, called the way the link window calls it: what goes on the wire,
/// what comes back, and what the user is told when the honest answer is that nothing is there. The bot's
/// half is tested in its own repository.
/// </summary>
public sealed class BotLinkClientTests
{
    private const string LinkedJson =
        """{"ok":true,"message":"Linked.","discordTag":"woozy","playerName":"woozy","accountId":246001782}""";

    private const string RefusedJson =
        """{"ok":false,"message":"That code has expired. Run /link again for a new one."}""";

    private static BotLinkClient Client(FakeHandler handler)
        => new(NullLogger<BotLinkClient>.Instance, handler);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://bot.example")]
    [InlineData("not a url")]
    public void AnAddressThatCannotBeABotIsRejected(string? typed)
        => Assert.Null(BotLinkClient.NormalizeBaseUrl(typed));

    [Theory]
    [InlineData("127.0.0.1:5099", "http://127.0.0.1:5099")]
    [InlineData("bot.example", "http://bot.example")]
    [InlineData("https://bot.example/", "https://bot.example")]
    [InlineData("  http://192.168.0.4:5099/  ", "http://192.168.0.4:5099")]
    public void ABareHostIsAssumedToSpeakPlainHttp(string typed, string expected)
        => Assert.Equal(expected, BotLinkClient.NormalizeBaseUrl(typed));

    [Fact]
    public async Task AClaimPutsTheWholeContractOnTheWire()
    {
        string? url = null;
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            url = request.RequestUri!.ToString();
            body = request.Content!.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, LinkedJson);
        });
        using var client = Client(handler);

        var result = await client.ClaimAsync("http://127.0.0.1:5099/", "OPT-7F3KQ", 246001782, "woozy",
            "https://discord.com/api/webhooks/123456789012345678/tracker-token");

        Assert.Equal("http://127.0.0.1:5099/v1/link/claim", url);
        Assert.Contains("\"code\":\"OPT-7F3KQ\"", body);
        Assert.Contains("\"accountId\":246001782", body);
        Assert.Contains("\"inGameName\":\"woozy\"", body);
        // The tracker webhook travels with the claim: the bot cannot watch anything until it has it.
        Assert.Contains("\"webhookUrl\":\"https://discord.com/api/webhooks/123456789012345678/tracker-token\"", body);
        // The bot records which client version asked, so it has to carry a real version, not a placeholder.
        Assert.Matches("\"clientVersion\":\"[0-9]+\\.[0-9]+\\.[0-9]+\"", body!);

        Assert.True(result.Ok);
        Assert.True(result.Value!.Ok);
        Assert.Equal("woozy", result.Value.DiscordTag);
        Assert.Equal(246001782, result.Value.AccountId);
    }

    [Fact]
    public async Task AClaimWithoutATrackerWebhookLeavesItOffTheWire()
    {
        // Linking without tracking is the default, and the bot's request shape has to stay compatible
        // with an app that never sets the field.
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, LinkedJson);
        });
        using var client = Client(handler);

        await client.ClaimAsync("http://127.0.0.1:5099", "OPT-7F3KQ", 246001782, "woozy");

        Assert.DoesNotContain("webhookUrl", body);
    }

    [Fact]
    public async Task ARefusedClaimArrivesAsAPayloadTheUiCanShow()
    {
        // The bot answers a refused code with 200 and ok:false, so the transport call succeeds and the
        // sentence in the payload is what the window shows. The two "ok" flags mean different things.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, RefusedJson));
        using var client = Client(handler);

        var result = await client.ClaimAsync("http://127.0.0.1:5099", "OPT-ZZZZZ", 246001782, "woozy");

        Assert.True(result.Ok);
        Assert.False(result.Value!.Ok);
        Assert.Equal("That code has expired. Run /link again for a new one.", result.Value.Message);
    }

    [Fact]
    public async Task ATrackerTestNamesTheWebhookAndReadsTheBotsAnswer()
    {
        string? url = null;
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            url = request.RequestUri!.ToString();
            body = request.Content!.ReadAsStringAsync().Result;
            return (HttpStatusCode.OK, """{"ok":true,"message":"Test image sent."}""");
        });
        using var client = Client(handler);

        var result = await client.TestTrackerAsync("https://bot.example",
            "https://discord.com/api/webhooks/123456789012345678/tracker-token");

        Assert.Equal("https://bot.example/v1/tracker/test", url);
        Assert.Contains("\"webhookUrl\":\"https://discord.com/api/webhooks/123456789012345678/tracker-token\"", body);
        Assert.True(result.Ok);
        Assert.True(result.Value!.Ok);
        Assert.Equal("Test image sent.", result.Value.Message);
    }

    [Fact]
    public async Task ARefusedTrackerTestArrivesAsAPayloadTheUiCanShow()
    {
        // The bot refuses a URL it cannot post to with 200 and ok:false, so the Settings row shows its
        // sentence rather than a transport error.
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            """{"ok":false,"message":"That does not look like a Discord webhook URL."}"""));
        using var client = Client(handler);

        var result = await client.TestTrackerAsync("http://127.0.0.1:5099", "not-a-webhook");

        Assert.True(result.Ok);
        Assert.False(result.Value!.Ok);
        Assert.Contains("Discord webhook URL", result.Value.Message);
    }

    [Fact]
    public async Task TheStatusCallNamesTheAccountItAsksAbout()
    {
        string? url = null;
        var handler = new FakeHandler(request =>
        {
            url = request.RequestUri!.ToString();
            return (HttpStatusCode.OK, """{"linked":true,"discordTag":"woozy","playerName":"woozy","accountId":246001782}""");
        });
        using var client = Client(handler);

        var result = await client.GetStatusAsync("https://bot.example", 246001782);

        Assert.Equal("https://bot.example/v1/link/status?accountId=246001782", url);
        Assert.True(result.Ok);
        Assert.True(result.Value!.Linked);
        Assert.Equal("woozy", result.Value.PlayerName);
    }

    [Fact]
    public async Task AnUnlinkedAccountIsAnAnswerNotAFailure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, """{"linked":false}"""));
        using var client = Client(handler);

        var result = await client.GetStatusAsync("http://127.0.0.1:5099", 246001782);

        Assert.True(result.Ok);
        Assert.False(result.Value!.Linked);
    }

    [Fact]
    public async Task ANothingThereAddressIsWordedForTheUser()
    {
        // The common case by far: the bot is not running yet, or the address in Settings is stale. The
        // message has to say both, because the user cannot tell which it was.
        using var client = Client(FakeHandler.AlwaysThrows());

        var claim = await client.ClaimAsync("http://127.0.0.1:5099", "OPT-7F3KQ", 246001782, "woozy");
        var status = await client.GetStatusAsync("http://127.0.0.1:5099", 246001782);

        Assert.False(claim.Ok);
        Assert.Contains("Nothing answered at http://127.0.0.1:5099", claim.Message);
        Assert.False(status.Ok);
        Assert.Contains("Nothing answered at http://127.0.0.1:5099", status.Message);

        // A dead local address is the one case where naming the community bot helps: whoever points
        // there is either self-hosted with the bot not running or still carrying the old local default.
        Assert.Contains(BotLinkClient.DefaultBaseUrl, claim.Message);
    }

    [Fact]
    public async Task AnUnreachableHostIsNotToldAboutTheCommunityBot()
    {
        // A self-hoster's own host being down gets its own sentence: pointing them at the community bot
        // would be advice to switch hosts instead of fixing theirs.
        using var client = Client(FakeHandler.AlwaysThrows());

        var claim = await client.ClaimAsync("https://bot.example", "OPT-7F3KQ", 246001782, "woozy");

        Assert.False(claim.Ok);
        Assert.Contains("Nothing answered at https://bot.example", claim.Message);
        Assert.DoesNotContain(BotLinkClient.DefaultBaseUrl, claim.Message);
    }

    [Fact]
    public async Task AnAddressThatIsNotOptimaBotIsWordedForTheUser()
    {
        // A reverse proxy, a captive portal or a typo answers HTML with a status code. That is not a
        // payload, and it must not throw into the window either.
        var handler = new FakeHandler(_ => (HttpStatusCode.NotFound, "<html><body>404 Not Found</body></html>"));
        using var client = Client(handler);

        var result = await client.GetStatusAsync("https://example.com", 246001782);

        Assert.False(result.Ok);
        Assert.Contains("did not recognise the request", result.Message);
    }

    [Fact]
    public async Task AnEmptyAnswerStillSaysWhatTheStatusWas()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.InternalServerError, string.Empty));
        using var client = Client(handler);

        var result = await client.ClaimAsync("http://127.0.0.1:5099", "OPT-7F3KQ", 246001782, "woozy");

        Assert.False(result.Ok);
        Assert.Contains("HTTP 500", result.Message);
    }
}
