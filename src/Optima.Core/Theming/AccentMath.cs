namespace Optima.Core.Theming;

/// <summary>
/// Pure color math for deriving the accent family (hover, pressed, glow, on-accent)
/// from a single user-chosen color. Lives in Core so it is unit-testable without WPF;
/// values travel as 0xAARRGGBB.
/// </summary>
public static class AccentMath
{
    public const string DefaultAccentHex = "#E8B45A";

    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB (leading # optional). Null when invalid.</summary>
    public static uint? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }
        if (s.Length == 6)
        {
            s = "FF" + s;
        }
        if (s.Length != 8 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            return null;
        }
        return argb;
    }

    /// <summary>WCAG relative luminance of the color, alpha ignored. 0 = black, 1 = white.</summary>
    public static double Luminance(uint argb)
    {
        static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        var r = Lin(((argb >> 16) & 0xFF) / 255.0);
        var g = Lin(((argb >> 8) & 0xFF) / 255.0);
        var b = Lin((argb & 0xFF) / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>WCAG contrast ratio between two colors (alpha ignored), always >= 1.</summary>
    public static double Contrast(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Moves the color toward white by <paramref name="amount"/> (0..1), keeping alpha.</summary>
    public static uint Lighten(uint argb, double amount) => Blend(argb, 0xFFFFFFFF, amount);

    /// <summary>Moves the color toward black by <paramref name="amount"/> (0..1), keeping alpha.</summary>
    public static uint Darken(uint argb, double amount) => Blend(argb, 0xFF000000, amount);

    /// <summary>Replaces the alpha channel (0..255).</summary>
    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>
    /// Ink for text on an accent fill: pure black or pure white, whichever contrasts more.
    /// The winner clears WCAG AA (4.5:1) for every possible accent; tinted inks do not.
    /// </summary>
    public static uint OnAccent(uint accent)
        => Contrast(accent, 0xFF000000) >= Contrast(accent, 0xFFFFFFFF) ? 0xFF000000u : 0xFFFFFFFFu;

    /// <summary>The full derived family for one accent, ready for the theme layer.</summary>
    public static AccentFamily Derive(uint accent) => new(
        Base: accent,
        Hover: Lighten(accent, 0.12),
        Pressed: Darken(accent, 0.10),
        Glow: WithAlpha(accent, 0x59),
        OnAccent: OnAccent(accent));

    private static uint Blend(uint argb, uint target, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        var a = (byte)((argb >> 24) & 0xFF);
        var r = Mix((byte)((argb >> 16) & 0xFF), (byte)((target >> 16) & 0xFF), amount);
        var g = Mix((byte)((argb >> 8) & 0xFF), (byte)((target >> 8) & 0xFF), amount);
        var b = Mix((byte)(argb & 0xFF), (byte)(target & 0xFF), amount);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }
}

/// <summary>Derived accent family as 0xAARRGGBB values.</summary>
public readonly record struct AccentFamily(uint Base, uint Hover, uint Pressed, uint Glow, uint OnAccent);
