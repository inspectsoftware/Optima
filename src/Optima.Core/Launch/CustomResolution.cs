using Optima.Core.Models;

namespace Optima.Core.Launch;

/// <summary>The decisions behind the custom resolution window, which sets a real monitor's mode without the virtual display.</summary>
public static class CustomResolution
{
    /// <summary>Reads what was typed as a size, within the limits <see cref="DisplayMode.IsValid"/> sets.</summary>
    public static bool TryParse(string? widthText, string? heightText, out int width, out int height)
    {
        height = 0;
        return int.TryParse(widthText?.Trim(), out width)
            & int.TryParse(heightText?.Trim(), out height)
            && new DisplayMode(width, height, 60).IsValid;
    }

    /// <summary>Whether Windows offers this size on the monitor at any refresh rate.</summary>
    public static bool IsListed(IEnumerable<DisplayMode> modes, int width, int height)
        => modes.Any(mode => mode.Width == width && mode.Height == height);

    /// <summary>
    /// The refresh rates to offer for a size, highest first. A size that is not listed has none of
    /// its own, so it gets the rate the monitor runs at now. Windows reports 0 and 1 for "the
    /// hardware's default", which is no rate to choose.
    /// </summary>
    public static IReadOnlyList<int> RatesFor(IEnumerable<DisplayMode> modes, int width, int height, int currentRate)
    {
        var rates = modes
            .Where(mode => mode.Width == width && mode.Height == height && mode.RefreshRate >= 24)
            .Select(mode => mode.RefreshRate)
            .Distinct()
            .OrderDescending()
            .ToList();
        return rates.Count > 0 ? rates : [currentRate];
    }

    /// <summary>Whether the monitor has been moved off the mode Windows has saved for it.</summary>
    public static bool CanRestore(DisplayMode current, DisplayMode? saved)
        => saved is { IsValid: true } mode && mode != current;
}
