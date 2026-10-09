namespace Optima.Core.Theming;

/// <summary>
/// One pool of light in the backdrop. Place and size are shares of the window; the colour is said
/// relative to the accent, so that changing the accent recolours every page without a table.
/// </summary>
/// <param name="Hue">Degrees the accent is turned by.</param>
/// <param name="Cool">0 is the accent, 1 is the theme's cool counter-colour.</param>
/// <param name="Alarm">0 to 1 toward red, for a launch that failed.</param>
public readonly record struct Pool(double X, double Y, double Rx, double Ry, double Hue, double Alpha, double Cool, double Alarm);

/// <summary>
/// The backdrop: three pools of light per page. Going to another page pours them from one
/// arrangement to the next; at rest nothing is computed or drawn again.
/// </summary>
public static class Pools
{
    /// <summary>The arrangement of a page. The same key always gives the same arrangement.</summary>
    public static Pool[] For(string key)
    {
        // FNV-1a, then a small generator: string.GetHashCode differs from run to run.
        var seed = 2166136261u;
        foreach (var c in key)
        {
            seed = (seed ^ c) * 16777619u;
        }
        double Next()
        {
            seed = seed * 1664525u + 1013904223u;
            return (seed >> 8 & 0xFFFF) / 65535.0;
        }

        return
        [
            // The accent, wide and shallow, somewhere in the upper half.
            new(0.25 + 0.5 * Next(), 0.16 + 0.26 * Next(), 0.6 + 0.2 * Next(), 0.2 + 0.08 * Next(), 0, 0.20, 0, 0),
            // A neighbour of the accent, lower down.
            new(0.12 + 0.76 * Next(), 0.55 + 0.3 * Next(), 0.3 + 0.14 * Next(), 0.3 + 0.12 * Next(), (Next() - 0.5) * 70, 0.11, 0, 0),
            // The cool counterweight, in one of the bottom corners.
            new(Next() < 0.5 ? 0.08 : 0.92, 0.9 + 0.1 * Next(), 0.3, 0.34, 0, 0.14, 1, 0),
        ];
    }

    /// <summary>A session starting or running: everything warms toward the accent and brightens.</summary>
    public static Pool[] Warm(Pool[] rest)
        => [.. rest.Select(pool => pool with { Alpha = Math.Min(0.4, pool.Alpha * 1.7), Cool = 0, Y = pool.Y * 0.85 })];

    /// <summary>A launch that failed: the same arrangement, in red.</summary>
    public static Pool[] Alarmed(Pool[] rest)
        => [.. rest.Select(pool => pool with { Alarm = 1, Alpha = Math.Min(0.4, pool.Alpha * 1.4) })];

    /// <summary>
    /// Where pool number <paramref name="index"/> is at <paramref name="t"/> (0 to 1) of a pour.
    /// Each pool sets off a little after the one before, swings out sideways from the straight
    /// line and swells on the way, which is what makes it read as liquid and not as a slide.
    /// </summary>
    public static Pool At(Pool from, Pool to, double t, int index)
    {
        var late = index * 0.12;
        var e = MotionSpec.EaseOut((t - late) / (1 - late));
        var arc = Math.Sin(Math.PI * e);
        var swing = (index % 2 == 0 ? 0.22 : -0.22) * arc;
        var swell = 1 + 0.15 * arc;
        double Mix(double a, double b) => a + (b - a) * e;
        return new Pool(
            Mix(from.X, to.X) - (to.Y - from.Y) * swing,
            Mix(from.Y, to.Y) + (to.X - from.X) * swing,
            Mix(from.Rx, to.Rx) * swell,
            Mix(from.Ry, to.Ry) * swell,
            Mix(from.Hue, to.Hue),
            Mix(from.Alpha, to.Alpha),
            Mix(from.Cool, to.Cool),
            Mix(from.Alarm, to.Alarm));
    }

    /// <summary>The pool's colour as ARGB, alpha included, for this accent and cool colour.</summary>
    public static uint Colour(Pool pool, uint accent, uint cool)
    {
        const uint Red = 0xFFE05A5A;
        var rgb = Blend(Blend(Turn(accent, pool.Hue), cool, pool.Cool), Red, pool.Alarm);
        return AccentMath.WithAlpha(rgb, (byte)Math.Round(255 * MotionSpec.Clamp(pool.Alpha)));
    }

    /// <summary>Turns a colour's hue, keeping how light it is.</summary>
    private static uint Turn(uint argb, double degrees)
    {
        if (degrees == 0)
        {
            return argb;
        }
        var c = Math.Cos(degrees * Math.PI / 180);
        var s = Math.Sin(degrees * Math.PI / 180);
        double r = argb >> 16 & 0xFF, g = argb >> 8 & 0xFF, b = argb & 0xFF;
        return Pack(
            r * (.299 + .701 * c + .168 * s) + g * (.587 - .587 * c + .330 * s) + b * (.114 - .114 * c - .497 * s),
            r * (.299 - .299 * c - .328 * s) + g * (.587 + .413 * c + .035 * s) + b * (.114 - .114 * c + .292 * s),
            r * (.299 - .300 * c + 1.25 * s) + g * (.587 - .588 * c - 1.05 * s) + b * (.114 + .886 * c - .203 * s));
    }

    private static uint Blend(uint a, uint b, double share)
    {
        double Mix(int shift) => (a >> shift & 0xFF) + ((b >> shift & 0xFF) - (double)(a >> shift & 0xFF)) * MotionSpec.Clamp(share);
        return Pack(Mix(16), Mix(8), Mix(0));
    }

    private static uint Pack(double r, double g, double b)
    {
        static uint Byte(double v) => (uint)Math.Round(Math.Max(0, Math.Min(255, v)));
        return 0xFF000000 | Byte(r) << 16 | Byte(g) << 8 | Byte(b);
    }
}