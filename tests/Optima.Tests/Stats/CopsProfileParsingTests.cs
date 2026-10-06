using Optima.Core.Stats;
using Xunit;

namespace Optima.Tests.Stats;

/// <summary>
/// The parts of the public profile the stats card made worth reading properly: level progress, clan
/// membership and standing. They are parsed here, from captured payloads, so the field names and the
/// types stay honest for whoever reads them - the card itself lives in the OptimaBot repository.
/// </summary>
public sealed class CopsProfileParsingTests
{
    private static CopsPlayerProfile Profile(string json)
    {
        var profile = CopsProfileParser.Parse(json);
        Assert.NotNull(profile);
        return profile!;
    }

    [Fact]
    public void LevelAndExperienceAreReadFromTheProfile()
    {
        var profile = Profile(CopsApiFixtures.RankedProfileResponse);

        Assert.Equal(164, profile.Level);
        Assert.Equal(19_760, profile.CurrentXp);
        Assert.Equal(20_000, profile.NextLevelXp);
        Assert.NotNull(profile.LevelProgress);
        Assert.InRange(profile.LevelProgress!.Value, 0.98, 0.99);
    }

    [Fact]
    public void ClanMembershipIsReadWhole()
    {
        var profile = Profile(CopsApiFixtures.RankedProfileResponse);

        Assert.Equal("PolarisGG", profile.ClanName);
        Assert.Equal("PLRS", profile.ClanTag);
        Assert.Equal(10, profile.ClanMemberRank);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 18, 10, 10, TimeSpan.Zero), profile.ClanJoinedAt);
    }

    [Fact]
    public void ARatedProfileCarriesItsRatingAndStanding()
    {
        var profile = Profile(CopsApiFixtures.RankedProfileResponse);

        Assert.Equal(5, profile.Rank);
        Assert.Equal(1570, profile.Mmr);
        Assert.Equal(6, profile.HighestRank);
        Assert.Equal(8_978, profile.LeaderboardPosition);
        Assert.Equal(18, profile.CurrentSeason!.Season);
    }

    [Fact]
    public void AProfileWithoutABanFieldIsNotBanned()
    {
        var profile = Profile(CopsApiFixtures.RankedProfileResponse);

        Assert.False(profile.Banned);
        Assert.Null(profile.BanDetail);
    }

    [Fact]
    public void ACleanStandingBanObjectIsNotABan()
    {
        // The trap the API sets: an ordinary account in clean standing answers with a ban object whose
        // clock is at zero and whose type is not a ban. Presence of the field is not evidence.
        var profile = Profile(CopsApiFixtures.RealProfileResponse);

        Assert.False(profile.Banned);
        Assert.Null(profile.BanDetail);
    }

    [Fact]
    public void ARunningBanIsABanAndSaysHowLongIsLeft()
    {
        var profile = Profile(CopsApiFixtures.BannedProfileResponse);

        Assert.True(profile.Banned);
        Assert.Equal("3h left", profile.BanDetail);
    }

}
