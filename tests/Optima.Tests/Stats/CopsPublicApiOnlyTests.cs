using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

/// <summary>
/// The rule that keeps accounts safe: Optima reads the public part of the Critical Ops API and
/// nothing else on that host. These tests fail if a request, or a future endpoint, steps outside it.
/// </summary>
public sealed class CopsPublicApiOnlyTests
{
    [Theory]
    [InlineData("https://default.prod.copsapi.criticalforce.fi/api/public/profile?ids=1", true)]
    [InlineData("https://default.prod.copsapi.criticalforce.fi/api/leaderboard/elite", false)]
    [InlineData("https://default.prod.copsapi.criticalforce.fi/api/public/../leaderboard/elite", false)]
    [InlineData("https://default.prod.copsapi.criticalforce.fi/api/", false)]
    [InlineData("https://default.prod.copsapi.criticalforce.fi/api/publicx/profile", false)]
    [InlineData("https://example.com/api/public/profile", false)]
    public void OnlyAddressesUnderThePublicRootAreAllowed(string url, bool allowed)
        => Assert.Equal(allowed, CopsApiClient.IsPublicEndpoint(new Uri(url)));

    [Fact]
    public async Task EveryLookupStaysUnderThePublicRoot()
    {
        var requested = new List<Uri>();
        using var handler = new FakeHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return (HttpStatusCode.OK, CopsApiFixtures.RankedProfileResponse);
        });
        using var client = new CopsApiClient(NullLogger<CopsApiClient>.Instance, handler);

        await client.LookupByNameAsync("Someone");
        await client.LookupByIdAsync(259425939);
        await client.LookupPlayerAsync("Someone", 259425939);

        Assert.NotEmpty(requested);
        Assert.All(requested, uri => Assert.StartsWith(CopsApiClient.PublicRoot, uri.AbsoluteUri, StringComparison.Ordinal));
    }
}
