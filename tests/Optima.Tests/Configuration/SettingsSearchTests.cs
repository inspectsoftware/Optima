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
}
