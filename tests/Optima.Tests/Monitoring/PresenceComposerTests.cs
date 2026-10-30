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
    public void InGameSaysPlayingWithWhoAndTheRankBelow()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("frosty · Gold 2 · 41W-23L", card.State);
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void InGameStatusLineShowsTheDetailsSoFriendsSeeMoreThanTheGameName()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge);

        Assert.Equal(PresenceStatusDisplay.Details, card.StatusDisplay);
    }

    [Fact]
    public void InGameWithoutBadgeKeepsItClean()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, null);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("", card.State);
        Assert.Null(card.RankTier);
        Assert.Equal("", card.SmallImageText);
    }

    [Fact]
    public void ZeroWinsPlayerShowsTheRecord()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, new PlayerSeasonBadge("rookie", 0, 4));

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("rookie · 0W-4L", card.State);
    }

    [Fact]
    public void ASeasonWithoutAMatchOmitsTheRecordRatherThanSayingZeroAndZero()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, new PlayerSeasonBadge("rookie", 0, 0));

        Assert.Equal("rookie", card.State);
    }

    [Fact]
    public void TheEmblemCaptionCarriesTheRatingOnlyWhenItIsChosen()
    {
        var standard = PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Standard);
        var full = PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Full);

        Assert.Equal("Gold 2", standard.SmallImageText);
        Assert.Equal("Gold 2 · 1440 MMR", full.SmallImageText);
    }

    [Fact]
    public void MinimalKeepsThePlayerOutOfTheCardEntirely()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Minimal);

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("", card.State);
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
    [InlineData(GamePresence.InGame, false, true, "frosty · 41W-23L")]
    [InlineData(GamePresence.InGame, true, false, "Gold 2 · 41W-23L")]
    [InlineData(GamePresence.InGame, true, true, "frosty · Gold 2 · 41W-23L")]
    [InlineData(GamePresence.InGame, false, false, "41W-23L")]
    [InlineData(GamePresence.Starting, false, true, "frosty")]
    [InlineData(GamePresence.Starting, true, false, "Gold 2")]
    [InlineData(GamePresence.Starting, true, true, "frosty · Gold 2")]
    [InlineData(GamePresence.Starting, false, false, "")]
    [InlineData(GamePresence.NotRunning, false, true, "frosty")]
    [InlineData(GamePresence.NotRunning, true, false, "Gold 2")]
    [InlineData(GamePresence.NotRunning, true, true, "frosty · Gold 2")]
    [InlineData(GamePresence.NotRunning, false, false, "")]
    public void RankAndNameAreEachRemovedOnTheirOwn(GamePresence presence, bool showRank, bool showName, string state)
    {
        var options = DiscordPresenceOptions.Standard with { ShowRank = showRank, ShowPlayerName = showName };

        var card = PresenceComposer.Compose(presence, Badge, options);

        Assert.Equal(state, card.State);
    }

    [Fact]
    public void TurningOffTheRecordLeavesTheNameAlone()
    {
        var options = DiscordPresenceOptions.Standard with { ShowRankedRecord = false };

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, options);

        Assert.Equal("frosty · Gold 2", card.State);
    }

    [Fact]
    public void TurningOffTheEmblemDropsTheSmallImageAndItsCaption()
    {
        var options = DiscordPresenceOptions.Standard with { ShowRankEmblem = false };

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, options);

        Assert.Null(card.RankTier);
        Assert.Equal("", card.SmallImageText);
        // The rank itself is a separate switch, so it stays on the live line.
        Assert.Equal("frosty · Gold 2 · 41W-23L", card.State);
    }

    [Fact]
    public void TheStatusLineFollowsTheChooser()
    {
        var options = DiscordPresenceOptions.Standard with { StatusDisplay = PresenceStatusDisplay.State };

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, options);

        Assert.Equal(PresenceStatusDisplay.State, card.StatusDisplay);
    }

    [Fact]
    public void TheLauncherCardFollowsTheSameSwitches()
    {
        var hidden = DiscordPresenceOptions.Standard with { ShowPlayerName = false, ShowRank = false };
        var shown = PresenceComposer.Compose(GamePresence.NotRunning, Badge);

        var card = PresenceComposer.Compose(GamePresence.NotRunning, Badge, hidden);

        Assert.Equal("frosty · Gold 2", shown.State);
        Assert.Equal("", card.State);
        // The emblem is a switch of its own, so hiding the name and the rank keeps it.
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void TheGameAndTheProjectAreBothClickable()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge);

        Assert.Equal("https://criticalopsgame.com/", card.DetailsUrl);
        Assert.Equal("https://github.com/inspectsoftware/Optima", card.LargeImageUrl);
    }

    [Fact]
    public void LaunchingShowsWhoIsLaunching()
    {
        var card = PresenceComposer.Compose(GamePresence.Starting, Badge);

        Assert.Equal("Launching", card.Details);
        Assert.Equal("frosty · Gold 2", card.State);
        Assert.Equal(4, card.RankTier);
        Assert.Equal("Optima", card.LargeImageText);
    }

    [Fact]
    public void LaunchingBeforeTheBadgeLoadsHasNoState()
    {
        var card = PresenceComposer.Compose(GamePresence.Starting, null);

        Assert.Equal("Launching", card.Details);
        Assert.Equal("", card.State);
    }

    [Fact]
    public void IdleIsTheLauncherCard()
    {
        var card = PresenceComposer.Compose(GamePresence.NotRunning, null);

        Assert.Equal("Idling...", card.Details);
        Assert.Equal("", card.State);
    }

    [Fact]
    public void TheLauncherCardShowsThePlayerOnceTheirRankIsKnown()
    {
        var card = PresenceComposer.Compose(GamePresence.NotRunning, Badge);

        Assert.Equal("Idling...", card.Details);
        Assert.Equal("frosty · Gold 2", card.State);
        Assert.Equal(4, card.RankTier);
    }

    [Fact]
    public void OverlongStringsAreTruncatedWithAnEllipsis()
    {
        var longName = new string('x', 200);
        var card = PresenceComposer.Compose(GamePresence.InGame, new PlayerSeasonBadge(longName, 1, 1));

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
        var baseline = PresenceComposer.Compose(GamePresence.InGame, Badge);

        Assert.NotEqual(baseline.Signature, PresenceComposer.Compose(GamePresence.InGame, null).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Full).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Minimal).Signature);
        Assert.NotEqual(baseline.Signature,
            PresenceComposer.Compose(GamePresence.NotRunning, Badge).Signature);
    }

    [Fact]
    public void AnIdenticalCardKeepsItsSignature()
    {
        var first = PresenceComposer.Compose(GamePresence.InGame, Badge);
        var second = PresenceComposer.Compose(GamePresence.InGame, Badge);

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

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, settings.EffectivePresenceOptions);

        Assert.Equal(expectName, card.State.Contains("frosty", StringComparison.Ordinal));
        Assert.Equal(expectRecord, card.State.Contains("41W-23L", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitChooserResultWinsOverTheLegacyText()
    {
        var settings = new AppSettings { DiscordPresenceDetail = "Minimal", DiscordPresenceOptions = DiscordPresenceOptions.Full };

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, settings.EffectivePresenceOptions);

        Assert.Equal("frosty · Gold 2 · 41W-23L", card.State);
        Assert.Equal("Gold 2 · 1440 MMR", card.SmallImageText);
    }

    private static readonly DateTimeOffset MatchStart = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InAMatchTheMapFollowsTheGame()
    {
        var match = new MatchState(MatchPhase.InMatch, "Raid", MatchMode.Unknown, MatchStart);

        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, match: match);

        Assert.Equal("Critical Ops · Raid", card.Details);
        Assert.Equal("frosty · Gold 2 · 41W-23L", card.State);
    }

    [Theory]
    [InlineData(MatchMode.Ranked, "Critical Ops · Ranked · Heat")]
    [InlineData(MatchMode.Casual, "Critical Ops · Casual · Heat")]
    [InlineData(MatchMode.Unknown, "Critical Ops · Heat")]
    public void AKnownModeIsNamedBeforeTheMap(MatchMode mode, string details)
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, match: new MatchState(MatchPhase.InMatch, "Heat", mode, MatchStart));

        Assert.Equal(details, card.Details);
    }

    [Fact]
    public void InTheGamesMenuTheStateSaysSo()
    {
        var card = PresenceComposer.Compose(GamePresence.InGame, Badge, match: new MatchState(MatchPhase.InMenu, Since: MatchStart));

        Assert.Equal("Critical Ops", card.Details);
        Assert.Equal("In menu · frosty · Gold 2 · 41W-23L", card.State);
    }

    [Fact]
    public void MinimalInAMatchStillHidesThePlayer()
    {
        var match = new MatchState(MatchPhase.InMatch, "Raid", MatchMode.Ranked, MatchStart);

        var inMatch = PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Minimal, match);
        var inMenu = PresenceComposer.Compose(GamePresence.InGame, Badge, DiscordPresenceOptions.Minimal, new MatchState(MatchPhase.InMenu));

        Assert.Equal("Critical Ops · Ranked · Raid", inMatch.Details);
        Assert.Equal("", inMatch.State);
        Assert.Equal("In menu", inMenu.State);
        Assert.Null(inMatch.RankTier);
    }

    [Fact]
    public void AMatchStateIsIgnoredOutsideTheGame()
    {
        var match = new MatchState(MatchPhase.InMatch, "Raid", MatchMode.Ranked, MatchStart);

        Assert.Equal("Idling...", PresenceComposer.Compose(GamePresence.NotRunning, Badge, match: match).Details);
        Assert.Equal("Launching", PresenceComposer.Compose(GamePresence.Starting, Badge, match: match).Details);
    }
}
