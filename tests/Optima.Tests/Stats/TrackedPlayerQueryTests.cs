using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

/// <summary>The one PLAYER box in Settings: how it is read, looked up, and what it stores.</summary>
public sealed class TrackedPlayerQueryTests
{
    private const string ProfileJson =
        """[{"basicInfo":{"userID":259425939,"name":"frosty","playerLevel":{"level":12}},"stats":{"seasonal_stats":[{"season":19,"ranked":{"k":120,"d":90,"a":30,"w":8,"l":4},"casual":{"k":0,"d":0,"a":0,"w":0,"l":0},"custom":{"k":0,"d":0,"a":0,"w":0,"l":0}}]}}]""";

    private static readonly CopsPlayerProfile Frosty = new(259425939, "frosty", 12, []);

    [Theory]
    [InlineData("48211907", 48211907L)]
    [InlineData("  42 ", 42L)]
    [InlineData("Kestrel", null)]
    [InlineData("12ab", null)]
    [InlineData("-5", null)]
    [InlineData("+5", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData("99999999999999999999", null)]
    public void DigitsOnlyReadAsAnAccountId(string typed, long? expected)
        => Assert.Equal(expected, TrackedPlayerQuery.AsAccountId(typed));

    [Fact]
    public void AFoundPlayerIsStoredByTheProfilesOwnNameAndId()
    {
        var decision = TrackedPlayerQuery.Decide("FROSTY", new CopsLookupResult(CopsLookupStatus.Found, Frosty, ""));

        Assert.Equal(TrackOutcome.Found, decision.Outcome);
        Assert.True(decision.Tracks);
        Assert.Equal("frosty", decision.Ign);
        Assert.Equal(259425939, decision.AccountId);
    }

    [Fact]
    public void AnUnreachableApiStoresDigitsAsTheId()
    {
        var decision = TrackedPlayerQuery.Decide("48211907", new CopsLookupResult(CopsLookupStatus.Error, null, ""));

        Assert.Equal(TrackOutcome.Unreachable, decision.Outcome);
        Assert.True(decision.Tracks);
        Assert.Equal("", decision.Ign);
        Assert.Equal(48211907, decision.AccountId);
    }

    [Fact]
    public void AnUnreachableApiStoresANameAsTyped()
    {
        var decision = TrackedPlayerQuery.Decide(" Kestrel ", new CopsLookupResult(CopsLookupStatus.Error, null, ""));

        Assert.True(decision.Tracks);
        Assert.Equal("Kestrel", decision.Ign);
        Assert.Null(decision.AccountId);
    }

    [Fact]
    public void ANotFoundPlayerChangesNothing()
    {
        var decision = TrackedPlayerQuery.Decide("nobody", CopsLookupResult.NotFoundPlayer);

        Assert.Equal(TrackOutcome.NotFound, decision.Outcome);
        Assert.False(decision.Tracks);
    }

    [Fact]
    public void AnEmptyBoxChangesNothing()
        => Assert.False(TrackedPlayerQuery.Decide("  ", CopsLookupResult.NotFoundPlayer).Tracks);

    [Fact]
    public async Task DigitsAreTriedAsAnIdFirst()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, ProfileJson));
        using var api = new CopsApiClient(NullLogger<CopsApiClient>.Instance, handler);

        var result = await TrackedPlayerQuery.LookupAsync(api, "259425939");

        Assert.True(result.IsFound);
        Assert.EndsWith("profile?ids=259425939", handler.LastUrl);
    }

    [Fact]
    public async Task DigitsNoAccountHasAreTriedAsAName()
    {
        // The API answers an unknown id with HTTP 500, like an unknown name.
        var handler = new FakeHandler(req =>
            req.RequestUri!.Query.Contains("ids=")
                ? (HttpStatusCode.InternalServerError, "Error")
                : (HttpStatusCode.OK, ProfileJson));
        using var api = new CopsApiClient(NullLogger<CopsApiClient>.Instance, handler);

        var result = await TrackedPlayerQuery.LookupAsync(api, "1337");

        Assert.True(result.IsFound);
        Assert.EndsWith("profile?usernames=1337", handler.LastUrl);
    }

    [Fact]
    public async Task ANameIsOnlyLookedUpAsAName()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, ProfileJson));
        using var api = new CopsApiClient(NullLogger<CopsApiClient>.Instance, handler);

        var result = await TrackedPlayerQuery.LookupAsync(api, "frosty");

        Assert.True(result.IsFound);
        Assert.EndsWith("profile?usernames=frosty", handler.LastUrl);
    }

    [Theory]
    [InlineData("Kestrel", 48211907L, "Kestrel", 48211907L)]
    [InlineData("Kestrel", 48211907L, " kestrel ", 48211907L)]
    [InlineData("Kestrel", 48211907L, "Falcon", null)]
    [InlineData("Kestrel", 48211907L, "", null)]
    [InlineData("", 48211907L, "", 48211907L)]
    public void ANameTypedForSomeoneElseDropsTheStoredId(string storedIgn, long? storedId, string typed, long? expected)
        => Assert.Equal(expected, TrackedPlayerQuery.IdForName(storedIgn, storedId, typed));

    [Fact]
    public void SavingTheSamePlayerAgainReplacesTheEntry()
    {
        var saved = new List<PlayerAccount> { new() { Key = "k1", Ign = "OldName", AccountId = 7, Label = "Main" } };

        var updated = TrackedPlayerQuery.WithSaved(saved, "NewName", 7);

        var only = Assert.Single(updated);
        Assert.Equal("k1", only.Key);
        Assert.Equal("NewName", only.Ign);
        Assert.Equal("Main", only.Label);
    }

    [Fact]
    public void SavingAnotherPlayerAddsAnEntry()
    {
        var saved = new List<PlayerAccount> { new() { Ign = "frosty", AccountId = 1 } };

        var updated = TrackedPlayerQuery.WithSaved(saved, "Kestrel", 2);

        Assert.Equal(["frosty", "Kestrel"], updated.Select(a => a.Ign));
    }

    [Theory]
    [InlineData("Kestrel", 48211907L, "Kestrel", 48211907L, true)]
    [InlineData("", 48211907L, "Kestrel", 48211907L, false)]
    [InlineData("osprey", null, "Osprey", 777L, false)]
    [InlineData("OldName", 7L, "NewName", 7L, false)]
    [InlineData("", 48211907L, "", 48211907L, true)]
    [InlineData("osprey", null, "osprey", null, true)]
    [InlineData("frosty", 7L, "frosty", 8L, false)]
    public void ASavedEntryHoldsThePlayerOnlyWithTheWholeIdentity(string ign, long? id, string trackedIgn, long? trackedId, bool expected)
        => Assert.Equal(expected, new PlayerAccount { Ign = ign, AccountId = id }.Holds(trackedIgn, trackedId, exactName: true));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ANameInAnotherCaseIsHeldUnlessItIsTheGamesSpelling(bool exactName, bool expected)
        => Assert.Equal(expected, new PlayerAccount { Ign = "Kestrel", AccountId = 48211907 }.Holds("kestrel", null, exactName));

    [Fact]
    public void SavingWithOnlyTheNameKeepsTheSavedId()
    {
        // Saved with the API up; saved again with it down, by a name typed in another case.
        var saved = new List<PlayerAccount> { new() { Key = "k", Ign = "Kestrel", AccountId = 48211907 } };

        var updated = Assert.Single(TrackedPlayerQuery.WithSaved(saved, "kestrel", null));

        Assert.Equal(("k", (long?)48211907), (updated.Key, updated.AccountId));
    }

    [Fact]
    public void SavingWithOnlyTheIdKeepsTheSavedName()
    {
        var saved = new List<PlayerAccount> { new() { Key = "k", Ign = "Kestrel", AccountId = 48211907 } };

        var updated = Assert.Single(TrackedPlayerQuery.WithSaved(saved, "", 48211907));

        Assert.Equal("Kestrel", updated.Ign);
    }

    [Fact]
    public void SavingTheFoundPlayerCompletesAnEntrySavedWhileTheApiWasDown()
    {
        var saved = new List<PlayerAccount> { new() { Key = "k", AccountId = 48211907 } };

        var updated = Assert.Single(TrackedPlayerQuery.WithSaved(saved, "Kestrel", 48211907));

        Assert.Equal(("k", "Kestrel", (long?)48211907), (updated.Key, updated.Ign, updated.AccountId));
    }

    [Theory]
    [InlineData("frosty", 7L, "FROSTY", 7L, true)]
    [InlineData("OldName", 7L, "NewName", 7L, true)]
    [InlineData("frosty", 7L, "frosty", 8L, false)]
    [InlineData("frosty", null, "frosty", 8L, true)]
    [InlineData("", 7L, "frosty", null, false)]
    [InlineData("", null, "", null, false)]
    public void TwoIdsDecideOnTheirOwnOtherwiseTheNames(string ign, long? id, string otherIgn, long? otherId, bool expected)
        => Assert.Equal(expected, new PlayerAccount { Ign = ign, AccountId = id }.Matches(otherIgn, otherId));

    [Fact]
    public void AnAccountSavedByIdAloneIsNamedByItsId()
        => Assert.Equal("48211907", new PlayerAccount { AccountId = 48211907 }.DisplayName);

    [Fact]
    public void TheSummaryCarriesLevelRankAndRankedRecord()
    {
        var profile = new CopsPlayerProfile(1, "Kestrel", 61,
            [new CopsSeasonStats(19, new CopsModeStats(118, 100, 0, 214, 188), CopsModeStats.Zero, CopsModeStats.Zero)],
            Rank: 4, Mmr: 1430);

        Assert.Equal("lv 61 · Gold 2 · ranked 214W-188L · K/D 1.18", TrackedPlayerQuery.Summary(profile));
    }

    [Fact]
    public void TheSummaryLeavesOutWhatTheProfileDoesNotHave()
        => Assert.Equal("lv 3", TrackedPlayerQuery.Summary(new CopsPlayerProfile(1, "rookie", 3, [])));

    [Fact]
    public void AnOlderConfigWithTrackedPlayersStillLoads()
    {
        // Written by a build that had the tracked-players list and the HOME strip for it.
        const string json = """
            {
              "playerIgn": "frosty",
              "playerAccountId": 259425939,
              "savedAccounts": [ { "key": "k1", "ign": "frosty", "accountId": 259425939 } ],
              "trackedPlayers": [ { "key": "k2", "ign": "Kestrel", "accountId": 48211907 } ],
              "homeWidgets": [ "status", "friends", "player" ]
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonStore.Options);

        Assert.NotNull(settings);
        Assert.Equal("frosty", settings!.PlayerIgn);
        Assert.Equal(259425939, settings.PlayerAccountId);
        Assert.Single(settings.SavedAccounts);
        // The layout keeps the id as written; HOME drops ids its catalog no longer has when it reads it.
        Assert.Equal(["status", "friends", "player"], settings.HomeWidgets!);
        Assert.DoesNotContain("trackedPlayers", JsonSerializer.Serialize(settings, JsonStore.Options));
    }
}
