using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class AccentMathTests
{
    [Theory]
    [InlineData("#E8B45A", 0xFFE8B45Au)]
    [InlineData("E8B45A", 0xFFE8B45Au)]
    [InlineData("#59E8B45A", 0x59E8B45Au)]
    [InlineData("#FA5", 0xFFFFAA55u)]
    [InlineData("  #e8b45a ", 0xFFE8B45Au)]
    public void ParsesValidHex(string input, uint expected)
        => Assert.Equal(expected, AccentMath.TryParse(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("notacolor")]
    [InlineData("#GGGGGG")]
    public void RejectsInvalidHex(string? input)
        => Assert.Null(AccentMath.TryParse(input));

    [Fact]
    public void DefaultAccentAlwaysParses()
        => Assert.NotNull(AccentMath.TryParse(AccentMath.DefaultAccentHex));

    [Fact]
    public void LuminanceEndpointsAreCorrect()
    {
        Assert.Equal(0.0, AccentMath.Luminance(0xFF000000), 3);
        Assert.Equal(1.0, AccentMath.Luminance(0xFFFFFFFF), 3);
    }

    [Fact]
    public void BlackOnWhiteContrastIsMaximal()
        => Assert.Equal(21.0, AccentMath.Contrast(0xFF000000, 0xFFFFFFFF), 1);

    [Fact]
    public void ContrastIsSymmetric()
        => Assert.Equal(
            AccentMath.Contrast(0xFFE8B45A, 0xFF0B0D12),
            AccentMath.Contrast(0xFF0B0D12, 0xFFE8B45A),
            6);

    [Fact]
    public void DarkPaletteTextTokensClearWcagAa()
    {
        const uint background = 0xFF0B0D12;
        Assert.True(AccentMath.Contrast(0xFF8089A0, background) >= 4.5); // muted
        Assert.True(AccentMath.Contrast(0xFFA6AEC2, background) >= 4.5); // secondary
        Assert.True(AccentMath.Contrast(0xFFE2E6F0, background) >= 4.5); // primary
    }

    [Fact]
    public void LightPaletteTextTokensClearWcagAa()
    {
        const uint background = 0xFFF2F1EE;
        Assert.True(AccentMath.Contrast(0xFF6B675E, background) >= 4.5); // muted
        Assert.True(AccentMath.Contrast(0xFF55514A, background) >= 4.5); // secondary
        Assert.True(AccentMath.Contrast(0xFF26241F, background) >= 4.5); // primary
    }

    [Fact]
    public void StatusHuesClearWcagAaInBothPalettes()
    {
        // Status colors render as small chip text, so they get the full 4.5:1 floor too.
        const uint darkBg = 0xFF0B0D12;
        foreach (var hue in new uint[] { 0xFF82C097, 0xFFD9B36A, 0xFFE08A8A, 0xFF8FA8CC })
        {
            Assert.True(AccentMath.Contrast(hue, darkBg) >= 4.5, $"dark status {hue:X8}");
        }

        const uint lightBg = 0xFFF2F1EE;
        foreach (var hue in new uint[] { 0xFF39734C, 0xFF8A661C, 0xFFA84848, 0xFF44618F })
        {
            Assert.True(AccentMath.Contrast(hue, lightBg) >= 4.5, $"light status {hue:X8}");
        }
    }

    [Fact]
    public void OnAccentFlipsWithAccentLuminance()
    {
        var onGold = AccentMath.OnAccent(0xFFE8B45A);
        var onNavy = AccentMath.OnAccent(0xFF203050);
        Assert.True(AccentMath.Luminance(onGold) < 0.2, "light accent gets dark ink");
        Assert.True(AccentMath.Luminance(onNavy) > 0.8, "dark accent gets light ink");
    }

    [Fact]
    public void OnAccentAlwaysReadable()
    {
        // Whatever accent the user picks, the ink on top of it must clear AA.
        uint[] samples = [0xFFE8B45A, 0xFF203050, 0xFFFF0000, 0xFF00FF00, 0xFF808080, 0xFF111111, 0xFFEEEEEE];
        foreach (var accent in samples)
        {
            var ink = AccentMath.OnAccent(accent);
            Assert.True(AccentMath.Contrast(accent, ink) >= 4.5, $"ink on {accent:X8} is {AccentMath.Contrast(accent, ink):F2}:1");
        }
    }

    [Fact]
    public void DeriveProducesLighterHoverAndDarkerPressed()
    {
        var family = AccentMath.Derive(0xFFE8B45A);
        Assert.True(AccentMath.Luminance(family.Hover) > AccentMath.Luminance(family.Base));
        Assert.True(AccentMath.Luminance(family.Pressed) < AccentMath.Luminance(family.Base));
    }

    [Fact]
    public void DeriveGlowKeepsColorWithSoftAlpha()
    {
        var family = AccentMath.Derive(0xFFE8B45A);
        Assert.Equal(0x59u, family.Glow >> 24);
        Assert.Equal(0x00E8B45Au, family.Glow & 0x00FFFFFF);
    }

    [Fact]
    public void WithAlphaReplacesOnlyAlpha()
        => Assert.Equal(0x80E8B45Au, AccentMath.WithAlpha(0xFFE8B45A, 0x80));

    [Fact]
    public void LightenAndDarkenAreClampedAtExtremes()
    {
        Assert.Equal(0xFFFFFFFFu, AccentMath.Lighten(0xFFE8B45A, 1.0));
        Assert.Equal(0xFF000000u, AccentMath.Darken(0xFFE8B45A, 1.0));
        Assert.Equal(0xFFE8B45Au, AccentMath.Lighten(0xFFE8B45A, 0.0));
    }
}
