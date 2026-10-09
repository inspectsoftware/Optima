using Optima.Core.Configuration;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Configuration;

/// <summary>
/// The display choice moved out of the profiles once. The first launch after that move has to do
/// what the last one before it did, for every kind of selection a player could have had.
/// </summary>
public sealed class DisplayChoiceTests
{
    private static readonly LaunchProfile Mine = new()
    {
        Name = "My 1440p",
        Display = new DisplayProfile { VirtualDisplay = true, Width = 2560, Height = 1440, RefreshRate = 144 },
    };

    private static readonly IReadOnlyList<LaunchProfile> Profiles = [.. ProfileService.BuiltInProfiles, Mine];

    private static AppSettings Migrate(AppSettings settings, bool fresh = false, bool driver = true)
        => DisplayChoice.Migrate(settings, Profiles, fresh, driver);

    [Theory]
    [InlineData("Competitive 1080p240", 1920, 1080, 240)]
    [InlineData("Competitive 1440p165", 2560, 1440, 165)]
    public void ARetiredBuiltInBecomesCompetitiveWithItsModeAsTheChoice(string selected, int width, int height, int hz)
    {
        var moved = Migrate(new AppSettings { SelectedProfileName = selected, FirstRunCompleted = true });

        Assert.Equal("Competitive", moved.SelectedProfileName);
        Assert.True(moved.VirtualDisplayEnabled);
        Assert.Equal(new DisplayMode(width, height, hz), moved.EffectiveDisplay.Mode);
        // The name it was moved to is a profile that exists.
        Assert.Contains(ProfileService.BuiltInProfiles, profile => profile.Name == moved.SelectedProfileName);
    }

    [Fact]
    public void AProfileOfThePlayersOwnHandsItsDisplayOver()
    {
        var moved = Migrate(new AppSettings { SelectedProfileName = "My 1440p", FirstRunCompleted = true });

        Assert.Equal("My 1440p", moved.SelectedProfileName);
        Assert.True(moved.VirtualDisplayEnabled);
        Assert.Equal(new DisplayMode(2560, 1440, 144), moved.EffectiveDisplay.Mode);
    }

    [Fact]
    public void SomeoneWhoPlayedOnTheirOwnScreenStillDoes()
    {
        // Driver installed (the setup does that), profile without a virtual display, Optima in use.
        var moved = Migrate(new AppSettings { SelectedProfileName = "Balanced", FirstRunCompleted = true }, driver: true);

        Assert.False(moved.VirtualDisplayEnabled);
        Assert.Equal("Balanced", moved.SelectedProfileName);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AFreshInstallGetsTheVirtualDisplayOnlyWhenItsDriverIsThere(bool driver, bool expected)
    {
        var moved = Migrate(new AppSettings(), fresh: true, driver);

        Assert.Equal(expected, moved.VirtualDisplayEnabled);
        Assert.Equal(new DisplayMode(1920, 1080, 240), moved.EffectiveDisplay.Mode);
        // The player's own monitor stays the main screen: the game opens there.
        Assert.False(moved.EffectiveDisplay.MakePrimary);
    }

    [Fact]
    public void AChoiceThatWasMadeIsNeverMovedAgain()
    {
        var chosen = new AppSettings { VirtualDisplayEnabled = false, SelectedProfileName = "Competitive 1080p240" };

        Assert.Same(chosen, Migrate(chosen));
    }

    [Fact]
    public void TheBuiltInsNoLongerSayAnythingAboutTheDisplay()
    {
        Assert.Equal(["Default", "Balanced", "Competitive"], ProfileService.BuiltInProfiles.Select(profile => profile.Name));
        Assert.All(ProfileService.BuiltInProfiles, profile => Assert.False(profile.Display.VirtualDisplay));
    }

    [Fact]
    public void ARestartIsOwedUntilThePcHasBeenRestartedSinceItWasAskedFor()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings { DriverRestartAskedAt = now.AddHours(-1) };

        // Up for two hours: the PC was running when the restart was asked for, and still is.
        Assert.True(settings.DriverRestartPending(now, TimeSpan.FromHours(2)));
        // Up for ten minutes: it has been restarted since.
        Assert.False(settings.DriverRestartPending(now, TimeSpan.FromMinutes(10)));
        Assert.False(new AppSettings().DriverRestartPending(now, TimeSpan.FromHours(2)));
    }
}
