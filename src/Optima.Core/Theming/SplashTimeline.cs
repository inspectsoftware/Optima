using static Optima.Core.Theming.MotionSpec;

namespace Optima.Core.Theming;

/// <summary>One of the four blades of the mark: how far it still is from home, and how visible.</summary>
public readonly record struct SplashBlade(double Offset, double Opacity, double Flash);

/// <summary>
/// One moment of the launch splash. Distances are in the logo's own units (the source artwork is
/// 1254 units square); rotations are in degrees. <see cref="Grow"/> runs 0 to 1 while the card grows
/// to the main window's bounds, and <see cref="Fade"/> runs 1 to 0 while it then lets the window through.
/// </summary>
public sealed record SplashFrame(
    double SquareScale,
    double SquareRotation,
    double SquareOpacity,
    IReadOnlyList<SplashBlade> Blades,
    double MarkRotation,
    double MarkScale,
    double Sweep,
    double SweepOpacity,
    IReadOnlyList<double> LetterOpacity,
    IReadOnlyList<double> LetterRise,
    double TextOpacity,
    double Grow,
    double Fade);

/// <summary>
/// The launch splash as a pure function of time. The mark is four blades around a square: the
/// square appears, the blades fly in clockwise and lock, the name is set. When the app is ready the
/// card grows to the main window's place and covers it, the mark and the name leaving as it grows;
/// the window is shown underneath, and once it has drawn the splash fades off it. Kept apart from
/// the drawing so the beats can be tested.
/// </summary>
public static class SplashTimeline
{
    /// <summary>The intro always plays in full, however fast startup was.</summary>
    public const double IntroMs = 1600;
    public const double GrowMs = 450;
    public const double RevealMs = 300;
    public const int Letters = 6;

    private const double BladeFirstMs = 200;
    private const double BladeStaggerMs = 130;
    private const double BladeTravelMs = 360;
    private const double BladeDistance = 420;

    /// <param name="t">Milliseconds since the splash appeared.</param>
    /// <param name="growT">Milliseconds since the card started to grow to the main window, or null while the app is still starting.</param>
    /// <param name="revealT">Milliseconds since the main window was drawn under the grown card, or null until then.</param>
    public static SplashFrame At(double t, double? growT = null, double? revealT = null)
    {
        var squareIn = Clamp(t / 250);
        var blades = new SplashBlade[4];
        for (var i = 0; i < blades.Length; i++)
        {
            var p = (t - BladeFirstMs - i * BladeStaggerMs) / BladeTravelMs;
            var landed = Clamp(p);
            // Brightest as the blade seats, gone a moment later.
            var flash = p is > 0.7 and < 1.7 ? Math.Sin(Math.PI * (p - 0.7)) : 0;
            blades[i] = new SplashBlade(BladeDistance * (1 - EaseOut(landed)), Clamp(p * 3), flash);
        }

        var lockIn = EaseOut(Clamp((t - 950) / 300));
        var sweep = Clamp((t - 1000) / 380);
        var letterOpacity = new double[Letters];
        var letterRise = new double[Letters];
        for (var i = 0; i < Letters; i++)
        {
            var p = Clamp((t - 1150 - i * 45) / 180);
            letterOpacity[i] = p;
            letterRise[i] = 8 * (1 - EaseOut(p));
        }
        var breathe = t > IntroMs ? 0.006 * Math.Sin((t - IntroMs) / 900) : 0;

        var frame = new SplashFrame(
            SquareScale: EaseOut(squareIn),
            SquareRotation: 45 * (1 - EaseOut(squareIn)),
            SquareOpacity: Clamp(t / 120),
            Blades: blades,
            MarkRotation: -8 * (1 - lockIn),
            MarkScale: 0.96 + 0.04 * lockIn + breathe,
            Sweep: sweep,
            SweepOpacity: sweep is > 0 and < 1 ? Math.Sin(Math.PI * sweep) : 0,
            LetterOpacity: letterOpacity,
            LetterRise: letterRise,
            TextOpacity: Clamp((t - 1400) / 200),
            Grow: 0,
            Fade: 1);

        if (growT is not { } g)
        {
            return frame;
        }

        var grow = EaseInOut(Clamp(g / GrowMs));
        var textGone = 1 - Clamp(g / 150);
        var markGone = 1 - Clamp(grow * 1.6);
        for (var i = 0; i < blades.Length; i++)
        {
            // Out the way they came.
            blades[i] = new SplashBlade(blades[i].Offset + 520 * grow, blades[i].Opacity * markGone, 0);
        }
        return frame with
        {
            SquareOpacity = frame.SquareOpacity * markGone,
            LetterOpacity = letterOpacity.Select(v => v * textGone).ToArray(),
            TextOpacity = frame.TextOpacity * textGone,
            SweepOpacity = 0,
            Grow = grow,
            // Solid until the window is drawn underneath: a card that let anything through before
            // then would show the desktop where the window is about to be. Slow to start, so a
            // window whose first frame reaches the screen a moment late is still covered.
            Fade = revealT is { } r ? 1 - EaseInOut(Clamp(r / RevealMs)) : 1,
        };
    }
}
