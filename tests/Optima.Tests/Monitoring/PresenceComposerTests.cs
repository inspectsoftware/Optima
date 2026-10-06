using Optima.Core.Models;
using Optima.Core.Monitoring;
using Xunit;

namespace Optima.Tests.Monitoring;

/// <summary>
/// The shape of the Discord card. Discord renders whatever it is given without complaint, so a wrong
/// card is not a crash: it is a card that quietly says the wrong thing to every friend. These pin the
/// wording, the links, which art each state points at, and that every field switch on the presence
/// chooser really removes the fact it names.
/// </summary>
public sealed class PresenceComposerTests
{
    private static readonly PlayerSeasonBadge Badge = new("frosty", 41, 23, "Gold 2", 4, 1440);

    [Fact]
    public void InGameLeadsWithTheGameAndTheRank()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, 141.4, Badge, "Competitive");

        Assert.Equal("Critical Ops · Gold 2", card.Details);
        Assert.Equal("frosty · 141 fps · 41W-23L", card.State);
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void InGameStatusLineShowsTheDetailsSoFriendsSeeMoreThanTheGameName()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null);

        Assert.Equal(PresenceStatusDisplay.Details, card.StatusDisplay);
    }

    [Fact]
    public void InGameWithoutBadgeFallsBackToFps()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, 59.6, null, null);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("60 fps", card.State);
        Assert.Null(card.RankTier);
        Assert.Equal("", card.SmallImageText);
    }

    [Fact]
    public void InGameWithoutFpsOrBadgeKeepsItClean()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, null, null, "Balanced");

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("", card.State);
    }

    [Fact]
    public void ZeroWinsPlayerShowsTheRecord()
    {
        var card = PresenceComposer.Compose(
            GamePresence.InGame, null, new PlayerSeasonBadge("rookie", 0, 4), null);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("rookie · 0W-4L", card.State);
    }

    [Fact]
    public void ASeasonWithoutAMatchOmitsTheRecordRatherThanSayingZeroAndZero()
    {
        var card = PresenceComposer.Compose(
            GamePresence.InGame, null, new PlayerSeasonBadge("rookie", 0, 0), null);

        Assert.Equal("rookie", card.State);
    }

    [Fact]
    public void TheEmblemCaptionCarriesTheRatingOnlyWhenItIsChosen()
    {
        var standard = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, DiscordPresenceOptions.Standard);
        var full = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, DiscordPresenceOptions.Full);

        Assert.Equal("Gold 2", standard.SmallImageText);
        Assert.Equal("Gold 2 · 1440 MMR", full.SmallImageText);
    }

    [Fact]
    public void MinimalKeepsThePlayerOutOfTheCardEntirely()
    {
        var card = PresenceComposer.Compose(
            GamePresence.InGame, 141.4, Badge, "Competitive", DiscordPresenceOptions.Minimal);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("141 fps", card.State);
        Assert.Null(card.RankTier);
        Assert.Equal("", card.SmallImageText);
        Assert.DoesNotContain("frosty", card.Details);
        Assert.DoesNotContain("frosty", card.State);
        Assert.DoesNotContain("Gold", card.State);
    }

    /// <summary>
    /// Each switch removes exactly the fact it names and leaves the rest of the card alone; a
    /// chooser that quietly drops a second field is worse than no chooser at all.
    /// </summary>
    [Theory]
    [InlineData(false, true, "Critical Ops", "frosty · 141 fps · 41W-23L")]
    [InlineData(true, false, "Critical Ops · Gold 2", "141 fps · 41W-23L")]
    [InlineData(true, true, "Critical Ops · Gold 2", "frosty · 141 fps · 41W-23L")]
    [InlineData(false, false, "Critical Ops", "141 fps · 41W-23L")]
    public void RankAndNameAreEachRemovedOnTheirOwn(bool showRank, bool showName, string details, string state)
    {
        var options = DiscordPresenceOptions.Standard with { ShowRank = showRank, ShowPlayerName = showName };

        var card = PresenceComposer.Compose(GamePresence.InGame, 141.4, Badge, null, options);

        Assert.Equal(details, card.Details);
        Assert.Equal(state, card.State);
    }

    [Fact]
    public void TurningOffFpsAndTheRecordLeavesTheNameAlone()
    {
        var options = DiscordPresenceOptions.Standard with { ShowFps = false, ShowRankedRecord = false };

        var card = PresenceComposer.Compose(GamePresence.InGame, 141.4, Badge, null, options);

        Assert.Equal("frosty", card.State);
    }

    [Fact]
    public void TurningOffTheEmblemDropsTheSmallImageAndItsCaption()
    {
        var options = DiscordPresenceOptions.Standard with { ShowRankEmblem = false };

        var card = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, options);

        Assert.Null(card.RankTier);
        Assert.Equal("", card.SmallImageText);
        // The rank itself is a separate switch, so it stays on the details line.
        Assert.Equal("Critical Ops · Gold 2", card.Details);
    }

    [Fact]
    public void TheStatusLineFollowsTheChooser()
    {
        var options = DiscordPresenceOptions.Standard with { StatusDisplay = PresenceStatusDisplay.State };

        var card = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, options);

        Assert.Equal(PresenceStatusDisplay.State, card.StatusDisplay);
    }

    [Fact]
    public void TheLauncherCardFollowsTheSameSwitches()
    {
        var hidden = DiscordPresenceOptions.Standard with { ShowPlayerName = false, ShowRank = false };
        var shown = PresenceComposer.Compose(GamePresence.NotRunning, null, Badge, null);

        var card = PresenceComposer.Compose(GamePresence.NotRunning, null, Badge, null, hidden);

        Assert.Equal("frosty · Gold 2", shown.State);
        Assert.Equal("Browsing the launcher", card.State);
        // The emblem is a switch of its own, so hiding the name and the rank keeps it.
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void TheGameAndTheProjectAreBothClickable()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null);

        Assert.Equal("https://criticalopsgame.com/", card.DetailsUrl);
        Assert.Equal("https://github.com/inspectsoftware/Optima", card.LargeImageUrl);
    }

    [Fact]
    public void LaunchingShowsTheProfileBeingApplied()
    {
        var card = PresenceComposer.Compose(GamePresence.Starting, null, Badge, "Competitive");

        Assert.Equal("Launching Critical Ops", card.Details);
        Assert.Equal("applying Competitive", card.State);
        Assert.Equal("Optima", card.LargeImageText);
    }

    [Fact]
    public void LaunchingWithoutAProfileOmitsTheState()
    {
        var card = PresenceComposer.Compose(GamePresence.Starting, null, Badge, " ");

        Assert.Equal("Launching Critical Ops", card.Details);
        Assert.Equal("", card.State);
    }

    [Fact]
    public void IdleIsTheLauncherCard()
    {
        var card = PresenceComposer.Compose(GamePresence.NotRunning, null, null, null);

        Assert.Equal("Optima Launcher", card.Details);
        Assert.Equal("Browsing the launcher", card.State);
    }

    [Fact]
    public void TheLauncherCardShowsThePlayerOnceTheirRankIsKnown()
    {
        var card = PresenceComposer.Compose(GamePresence.NotRunning, null, Badge, null);

        Assert.Equal("Optima Launcher", card.Details);
        Assert.Equal("frosty · Gold 2", card.State);
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void OverlongStringsAreTruncatedWithAnEllipsis()
    {
        var longName = new string('x', 200);
        var card = PresenceComposer.Compose(
            GamePresence.InGame, 100, new PlayerSeasonBadge(longName, 1, 1), null);

        Assert.True(card.State.Length <= 120);
        Assert.EndsWith("…", card.State);
    }

    /// <summary>
    /// The signature is what decides whether a new card is pushed at all, so a field that reaches
    /// Discord but not the signature is a field that silently stops updating.
    /// </summary>
    [Fact]
    public void EveryVisibleChangeMovesTheSignature()
    {
        var baseline = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null);

        Assert.NotEqual(baseline.Signature, PresenceComposer.Compose(GamePresence.InGame, 61, Badge, null).Signature);
        Assert.NotEqual(baseline.Signature, PresenceComposer.Compose(GamePresence.InGame, 60, null, null).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, DiscordPresenceOptions.Full).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, DiscordPresenceOptions.Minimal).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.NotRunning, 60, Badge, null).Signature);
    }

    [Fact]
    public void AnIdenticalCardKeepsItsSignature()
    {
        var first = PresenceComposer.Compose(GamePresence.InGame, 60.2, Badge, null);
        var second = PresenceComposer.Compose(GamePresence.InGame, 60.4, Badge, null);

        // 60.2 and 60.4 both render as "60 fps", so nothing about the card actually changed.
        Assert.Equal(first.Signature, second.Signature);
    }

    /// <summary>
    /// A configuration written before the chooser existed only has the old three-step text, so the
    /// step has to keep meaning what it meant: anything unrecognized falls back to Standard.
    /// </summary>
    [Theory]
    [InlineData("Minimal", false, false)]
    [InlineData("Standard", true, true)]
    [InlineData("Full", true, true)]
    [InlineData("nonsense", true, true)]
    [InlineData("", true, true)]
    public void LegacyDetailTextStillDecidesTheCard(string detail, bool expectName, bool expectRecord)
    {
        var settings = new AppSettings { DiscordPresenceDetail = detail };

        var card = PresenceComposer.Compose(
            GamePresence.InGame, 60, Badge, null, settings.EffectivePresenceOptions);

        Assert.Equal(expectName, card.State.Contains("frosty", StringComparison.Ordinal));
        Assert.Equal(expectRecord, card.State.Contains("41W-23L", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitChooserResultWinsOverTheLegacyText()
    {
        var settings = new AppSettings { DiscordPresenceDetail = "Minimal", DiscordPresenceOptions = DiscordPresenceOptions.Full };

        var card = PresenceComposer.Compose(GamePresence.InGame, 60, Badge, null, settings.EffectivePresenceOptions);

        Assert.Equal("frosty · 60 fps · 41W-23L", card.State);
        Assert.Equal("Gold 2 · 1440 MMR", card.SmallImageText);
    }
}
