using Optima.Core.Configuration;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("start", true)]
    [InlineData("WINDOWS start", true)]
    [InlineData("sign-in", true)]
    [InlineData("start linux", false)]
    [InlineData("tray minimised", true)]
    public void EveryWordHasToBeInTheLabelOrTheHint(string query, bool expected)
        => Assert.Equal(expected, SettingsSearch.Matches(query, "Start with Windows", "Start minimised to the tray at sign-in."));

    [Fact]
    public void ARowWithoutAHintIsMatchedOnItsLabel()
        => Assert.True(SettingsSearch.Matches("saved", "Saved in Optima", null));

    [Theory]
    [InlineData("discord", true)]
    [InlineData("discord presence", true)]
    [InlineData("network discord", true)]
    [InlineData("watchdog", false)]
    public void ASectionOrCardNameFindsItsRows(string query, bool expected)
        => Assert.Equal(expected, SettingsSearch.Matches(query, "Application ID", "Optima's own app. Clear it to keep presence off.",
            "Presence goes to the Discord app on this PC; the only request over the network asks discord.com.", "PRESENCE", "Discord"));
}
