using Optima.Core.Theming;
using Xunit;

namespace Optima.Tests.Theming;

public sealed class PoolsTests
{
    [Fact]
    public void APageAlwaysHasTheSameArrangement_AndPagesDiffer()
    {
        Assert.Equal(Pools.For("HomeViewModel"), Pools.For("HomeViewModel"));
        Assert.NotEqual(Pools.For("HomeViewModel"), Pools.For("PlayViewModel"));
        Assert.All(Pools.For("DisplayViewModel"), pool =>
        {
            Assert.InRange(pool.X, 0, 1);
            Assert.InRange(pool.Y, 0, 1);
            Assert.InRange(pool.Alpha, 0.05, 0.4);
        });
    }

    [Fact]
    public void APourStartsWhereItWasAndEndsWhereItIsSent()
    {
        var from = Pools.For("HomeViewModel");
        var to = Pools.Alarmed(Pools.For("PlayViewModel"));

        for (var i = 0; i < from.Length; i++)
        {
            Assert.Equal(from[i], Pools.At(from[i], to[i], 0, i));
            var end = Pools.At(from[i], to[i], 1, i);
            Assert.Equal(to[i].X, end.X, 9);
            Assert.Equal(to[i].Y, end.Y, 9);
            Assert.Equal(to[i].Rx, end.Rx, 9);
            Assert.Equal(1, end.Alarm, 9);
        }
    }

    [Fact]
    public void TheColourComesFromTheAccent()
    {
        var plain = new Pool(0.5, 0.5, 0.5, 0.5, Hue: 0, Alpha: 1, Cool: 0, Alarm: 0);

        Assert.Equal(0xFFE8B45Au, Pools.Colour(plain, 0xFFE8B45A, 0xFF8FA8CC));
        Assert.Equal(0xFF8FA8CCu, Pools.Colour(plain with { Cool = 1 }, 0xFFE8B45A, 0xFF8FA8CC));
        Assert.Equal(0xFFE05A5Au, Pools.Colour(plain with { Alarm = 1 }, 0xFFE8B45A, 0xFF8FA8CC));
        Assert.Equal(0x80u, Pools.Colour(plain with { Alpha = 0.5 }, 0xFFE8B45A, 0xFF8FA8CC) >> 24);
        // Turned, it is another colour of about the same weight, not grey and not black.
        var turned = Pools.Colour(plain with { Hue = 35 }, 0xFFE8B45A, 0xFF8FA8CC);
        Assert.NotEqual(0xFFE8B45Au, turned);
        Assert.InRange(AccentMath.Luminance(turned) / AccentMath.Luminance(0xFFE8B45A), 0.6, 1.6);
    }
}