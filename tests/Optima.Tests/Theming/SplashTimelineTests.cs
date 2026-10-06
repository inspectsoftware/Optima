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
    public void Opening_ClearsTheStage_AndEndsWithNothingLeft()
    {
        var start = SplashTimeline.At(4000, 0);
        Assert.Equal(0, start.Open, 6);
        Assert.Equal(1, start.Fade, 6);

        var end = SplashTimeline.At(4000 + SplashTimeline.OpenMs, SplashTimeline.OpenMs);
        Assert.Equal(1, end.Open, 6);
        Assert.Equal(0, end.Fade, 6);
        Assert.Equal(0, end.SquareFill, 6);
        Assert.All(end.Blades, blade => Assert.Equal(0, blade.Opacity, 6));
        Assert.All(end.LetterOpacity, opacity => Assert.Equal(0, opacity, 6));
    }
}
