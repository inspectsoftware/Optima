using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class SplashTimelineTests
{
    [Fact]
    public void WhenTheIntroEnds_TheMarkIsWholeAndStill_AndTheNameIsSet()
    {
        var end = SplashTimeline.At(SplashTimeline.IntroMs);

        Assert.All(end.Blades, blade =>
        {
            Assert.Equal(0, blade.Offset, 6);
            Assert.Equal(1, blade.Opacity, 6);
            Assert.Equal(0, blade.Flash, 6);
        });
        Assert.Equal(1, end.SquareScale, 6);
        Assert.Equal(0, end.SquareRotation, 6);
        Assert.Equal(0, end.MarkRotation, 6);
        Assert.Equal(1, end.MarkScale, 6);
        Assert.Equal(0, end.SweepOpacity, 6);
        Assert.All(end.LetterOpacity, opacity => Assert.Equal(1, opacity, 6));
        Assert.Equal(1, end.TextOpacity, 6);
        Assert.Equal(0, end.Grow, 6);
        Assert.Equal(1, end.Fade, 6);
    }

    [Fact]
    public void TheSquareComesFirst_ThenTheBladesClockwise()
    {
        var early = SplashTimeline.At(180);
        Assert.True(early.SquareOpacity > 0.9);
        Assert.All(early.Blades, blade => Assert.Equal(0, blade.Opacity, 6));

        // Mid-assembly no blade is further along than the one before it. (Compared by how far it
        // has appeared, not by distance: a landing blade overshoots home before it settles.)
        var mid = SplashTimeline.At(420);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(mid.Blades[i].Opacity >= mid.Blades[i + 1].Opacity);
        }
        Assert.True(mid.Blades[0].Opacity > mid.Blades[3].Opacity);
    }

    [Fact]
    public void TheNameIsSetLetterByLetter()
    {
        var mid = SplashTimeline.At(1300);

        for (var i = 0; i < SplashTimeline.Letters - 1; i++)
        {
            Assert.True(mid.LetterOpacity[i] >= mid.LetterOpacity[i + 1]);
        }
        Assert.True(mid.LetterOpacity[0] > mid.LetterOpacity[^1]);
    }

    [Fact]
    public void Growing_CoversTheWindowWithSolidGround_AndClearsTheStage()
    {
        // Until the window is drawn underneath, nothing of the card may let the desktop through.
        for (var g = 0.0; g <= SplashTimeline.GrowMs + 1000; g += 25)
        {
            Assert.Equal(1, SplashTimeline.At(4000 + g, g).Fade, 6);
        }

        Assert.Equal(0, SplashTimeline.At(4000, 0).Grow, 6);
        var grown = SplashTimeline.At(4000 + SplashTimeline.GrowMs, SplashTimeline.GrowMs);
        Assert.Equal(1, grown.Grow, 6);
        Assert.Equal(0, grown.SquareOpacity, 6);
        Assert.Equal(0, grown.TextOpacity, 6);
        Assert.All(grown.Blades, blade => Assert.Equal(0, blade.Opacity, 6));
        Assert.All(grown.LetterOpacity, opacity => Assert.Equal(0, opacity, 6));
    }

    [Fact]
    public void TheReveal_FadesTheGrownCardAway_WithoutMovingIt()
    {
        var grown = SplashTimeline.GrowMs + 200;

        var start = SplashTimeline.At(4000, grown, 0);
        Assert.Equal(1, start.Fade, 6);
        // Slow to start: the window's first frame may reach the screen a moment after it was drawn.
        Assert.True(SplashTimeline.At(4016, grown + 16, 16).Fade > 0.99);

        var mid = SplashTimeline.At(4000 + SplashTimeline.RevealMs / 2, grown, SplashTimeline.RevealMs / 2);
        Assert.InRange(mid.Fade, 0.01, 0.99);
        Assert.Equal(1, mid.Grow, 6);

        Assert.Equal(0, SplashTimeline.At(4000 + SplashTimeline.RevealMs, grown, SplashTimeline.RevealMs).Fade, 6);
    }
}
