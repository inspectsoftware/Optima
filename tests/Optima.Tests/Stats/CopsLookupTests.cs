using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

/// <summary>Exercises the real request/response handling of CopsApiClient over a fake handler.</summary>
public sealed class CopsLookupTests
{
    private const string ProfileJson =
        """[{"basicInfo":{"userID":259425939,"name":"frosty","playerLevel":{"level":12}},"stats":{"seasonal_stats":[{"season":19,"ranked":{"k":120,"d":90,"a":30,"w":8,"l":4},"casual":{"k":40,"d":30,"a":10,"w":3,"l":2},"custom":{"k":0,"d":0,"a":0,"w":0,"l":0}}]}}]""";

    private const string SeasonlessProfileJson =
        """[{"basicInfo":{"userID":7,"name":"rookie"}}]""";

    private static CopsApiClient Client(FakeHandler handler)
        => new(NullLogger<CopsApiClient>.Instance, handler);

    [Fact]
    public async Task LookupByIdFindsThePlayer()
    {
        var handler = new FakeHandler(req => (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupByIdAsync(259425939);

        Assert.True(result.IsFound);
        Assert.Equal("frosty", result.Profile!.Name);
        Assert.EndsWith("profile?ids=259425939", handler.LastUrl);
    }

    [Fact]
    public async Task LookupByNameIsCaseInsensitiveOnTheServer()
    {
        var handler = new FakeHandler(req => (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupByNameAsync("FROSTY");

        Assert.True(result.IsFound);
        Assert.Equal("frosty", result.Profile!.Name);
    }

    [Fact]
    public async Task LookupPlayerPrefersTheAccountId()
    {
        var handler = new FakeHandler(req => (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupPlayerAsync("frosty", 259425939);

        Assert.True(result.IsFound);
        Assert.Contains("ids=", handler.LastUrl);
    }

    [Fact]
    public async Task LookupPlayerFallsBackToTheNameWhenTheIdMisses()
    {
        // The API answers unknown players with HTTP 500, which must not block the name fallback.
        var handler = new FakeHandler(req =>
            req.RequestUri!.Query.Contains("ids=")
                ? (HttpStatusCode.InternalServerError, "Error")
                : (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupPlayerAsync("frosty", 111);

        Assert.True(result.IsFound);
        Assert.Equal("frosty", result.Profile!.Name);
        Assert.EndsWith("profile?usernames=frosty", handler.LastUrl);
    }

    [Fact]
    public async Task LookupPlayerUsesTheNameWhenNoIdIsSet()
    {
        var handler = new FakeHandler(req => (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupPlayerAsync("frosty", null);

        Assert.True(result.IsFound);
        Assert.Contains("usernames=", handler.LastUrl);
    }

    [Fact]
    public async Task MissedLookupsAreNotFoundNotErrors()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.InternalServerError, "Error"));
        using var client = Client(handler);

        var byName = await client.LookupByNameAsync("nobody");
        var byId = await client.LookupByIdAsync(12345);

        Assert.Equal(CopsLookupStatus.NotFound, byName.Status);
        Assert.Equal(CopsLookupStatus.NotFound, byId.Status);
        Assert.False(byName.IsFound);
    }

    [Fact]
    public async Task NetworkFailuresAreErrorsDistinctFromNotFound()
    {
        using var client = new CopsApiClient(NullLogger<CopsApiClient>.Instance, FakeHandler.AlwaysThrows());

        var result = await client.LookupPlayerAsync("frosty", 259425939);

        Assert.Equal(CopsLookupStatus.Error, result.Status);
        Assert.False(result.IsFound);
    }

    [Fact]
    public async Task EmptyInputsReportNotFoundWithoutTouchingTheNetwork()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var client = Client(handler);

        Assert.False((await client.LookupPlayerAsync("", null)).IsFound);
        Assert.Null(handler.LastUrl);
    }

    [Fact]
    public async Task PlaceholderIdWithADifferentNameResolvesByName()
    {
        // A typo'd id comes back as a scaffold OPS-000000111 account; the name must win.
        var handler = new FakeHandler(req =>
            req.RequestUri!.Query.Contains("ids=")
                ? (HttpStatusCode.OK, """[{"basicInfo":{"userID":111,"name":"OPS-000000111"},"stats":{"seasonal_stats":[]}}]""")
                : (HttpStatusCode.OK, ProfileJson));
        using var client = Client(handler);

        var result = await client.LookupPlayerAsync("frosty", 111);

        Assert.True(result.IsFound);
        Assert.Equal("frosty", result.Profile!.Name);
        Assert.Equal(259425939, result.Profile.UserId);
    }

    [Fact]
    public void PlaceholderDetectionSpotsTheScaffoldProfile()
    {
        var placeholder = CopsProfileParser.Parse(
            """{"basicInfo":{"userID":99999999,"name":"OPS-099999999"},"stats":{"seasonal_stats":[{"season":19,"ranked":{"k":0,"d":0,"a":0,"w":0,"l":0}}]}}""");
        Assert.NotNull(placeholder);
        Assert.True(CopsApiClient.LooksLikePlaceholderProfile(placeholder!));

        var real = CopsProfileParser.Parse(ProfileJson);
        Assert.NotNull(real);
        Assert.False(CopsApiClient.LooksLikePlaceholderProfile(real!));
    }

    [Fact]
    public async Task SeasonlessProfilesAreFoundWithEmptySeasons()
    {
        var handler = new FakeHandler(req => (HttpStatusCode.OK, SeasonlessProfileJson));
        using var client = Client(handler);

        var result = await client.LookupByNameAsync("rookie");

        Assert.True(result.IsFound);
        Assert.Null(result.Profile!.CurrentSeason);
    }
}

/// <summary>Fake HttpClientHandler that answers every request from a callback.</summary>
internal sealed class FakeHandler : HttpClientHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _responder;

    public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

    public string? LastUrl { get; private set; }

    public static FakeHandler AlwaysThrows() => new(_ => throw new HttpRequestException("simulated network outage"));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        LastUrl = uri.AbsolutePath + uri.Query;
        var (status, body) = _responder(request);
        return await Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
