namespace Optima.Core.Theming;

/// <summary>One of the four blades of the mark: how far it still is from home, and how visible.</summary>
public readonly record struct SplashBlade(double Offset, double Opacity, double Flash);

/// <summary>
/// One moment of the launch splash. Distances are in the logo's own units (the source artwork is
/// 1254 units square); rotations are in degrees. <see cref="Open"/> runs 0 to 1 while the centre
/// square opens into the main window.
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
    double Open,
    double SquareFill,
    double Fade);

/// <summary>
/// The launch splash as a pure function of time. The mark is four blades around a square: the
/// square appears, the blades fly in clockwise and lock, the name is set, and when the app is ready
/// the square opens into the main window. Kept apart from the drawing so the beats can be tested.
/// </summary>
public static class SplashTimeline
{
    /// <summary>The intro always plays in full, however fast startup was.</summary>
    public const double IntroMs = 1600;
    public const double OpenMs = 450;
    public const int Letters = 6;

    private const double BladeFirstMs = 200;
    private const double BladeStaggerMs = 130;
    private const double BladeTravelMs = 360;
    private const double BladeDistance = 420;

    /// <param name="t">Milliseconds since the splash appeared.</param>
    /// <param name="openT">Milliseconds since the app became ready, or null while it is still starting.</param>
    public static SplashFrame At(double t, double? openT = null)
    {
        var squareIn = Clamp(t / 250);
        var blades = new SplashBlade[4];
        for (var i = 0; i < blades.Length; i++)
        {
            var p = (t - BladeFirstMs - i * BladeStaggerMs) / BladeTravelMs;
            var landed = Clamp(p);
            // Brightest as the blade seats, gone a moment later.
            var flash = p is > 0.7 and < 1.7 ? Math.Sin(Math.PI * (p - 0.7)) : 0;
            blades[i] = new SplashBlade(BladeDistance * (1 - Back(landed)), Clamp(p * 3), flash);
        }

        var lockIn = Back(Clamp((t - 950) / 300));
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
            SquareScale: Back(squareIn),
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
            Open: 0,
            SquareFill: 1,
            Fade: 1);

        if (openT is not { } o)
        {
            return frame;
        }

        var open = EaseInOut(Clamp(o / OpenMs));
        var textGone = 1 - Clamp(o / 150);
        for (var i = 0; i < blades.Length; i++)
        {
            // Out the way they came.
            blades[i] = new SplashBlade(blades[i].Offset + 520 * open, blades[i].Opacity * (1 - Clamp(open * 1.6)), 0);
        }
        return frame with
        {
            LetterOpacity = letterOpacity.Select(v => v * textGone).ToArray(),
            TextOpacity = frame.TextOpacity * textGone,
            SweepOpacity = 0,
            Open = open,
            SquareFill = 1 - Clamp(o / 200),
            Fade = 1 - Clamp((o / OpenMs - 0.55) / 0.45),
        };
    }

    private static double Clamp(double x) => Math.Max(0, Math.Min(1, x));

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    private static double EaseInOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    private static double Back(double x) => 1 + 2.70158 * Math.Pow(x - 1, 3) + 1.70158 * Math.Pow(x - 1, 2);
}
