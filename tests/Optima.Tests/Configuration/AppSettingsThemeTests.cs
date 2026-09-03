using System.Text.Json;
using Optima.Core.Models;
using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class AppSettingsThemeTests
{
    [Fact]
    public void DefaultsAreDarkWithGold()
    {
        var settings = new AppSettings();
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("#E8B45A", settings.AccentColor);
        Assert.NotNull(AccentMath.TryParse(settings.AccentColor));
    }

    [Fact]
    public void ThemeAndAccentSurviveJsonRoundTrip()
    {
        var original = new AppSettings { Theme = "Light", AccentColor = "#6FB7E8" };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("Light", loaded!.Theme);
        Assert.Equal("#6FB7E8", loaded.AccentColor);
    }

    [Fact]
    public void OlderConfigWithoutThemeFieldsFallsBackToDefaults()
    {
        // A config.json written by v0.1.x has no Theme/AccentColor properties.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"FirstRunCompleted":true}""");

        Assert.NotNull(loaded);
        Assert.True(loaded!.FirstRunCompleted);
        Assert.Equal("Dark", loaded.Theme);
        Assert.NotNull(AccentMath.TryParse(loaded.AccentColor));
    }
}
