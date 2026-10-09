using Optima.Core.Models;

namespace Optima.Core.Configuration;

/// <summary>
/// The one-time move of the display choice out of the profiles and into the settings.
///
/// Until 0.8.0 every profile said for itself whether the game ran on the virtual display and at
/// which resolution, and two of the built-in profiles existed only to carry a resolution. From
/// 0.8.0 there is one choice, made on the Display page. This fills that choice in, once, from
/// whatever the player had selected, so that the first launch after the update does what the last
/// one before it did.
/// </summary>
public static class DisplayChoice
{
    /// <summary>The built-in the two resolution-named ones became.</summary>
    public const string Competitive = "Competitive";

    // The built-in profiles that were named after a mode, and the mode each of them set.
    private static readonly IReadOnlyDictionary<string, DisplayMode> RetiredBuiltIns =
        new Dictionary<string, DisplayMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["Competitive 1080p240"] = new(1920, 1080, 240),
            ["Competitive 1440p165"] = new(2560, 1440, 165),
        };

    /// <summary>
    /// The settings with the display choice filled in, or the same settings when it already is.
    /// </summary>
    /// <param name="profiles">The profiles as they are now, the player's own included.</param>
    /// <param name="freshInstall">True on a PC where Optima has not been set up before this start.</param>
    /// <param name="driverInstalled">Whether the virtual display driver is on this PC.</param>
    public static AppSettings Migrate(
        AppSettings settings, IReadOnlyList<LaunchProfile> profiles, bool freshInstall, bool driverInstalled)
    {
        if (settings.VirtualDisplayEnabled is not null)
        {
            return settings;
        }

        if (RetiredBuiltIns.TryGetValue(settings.SelectedProfileName, out var retired))
        {
            return With(settings, enabled: true, retired) with { SelectedProfileName = Competitive };
        }

        var selected = profiles.FirstOrDefault(p =>
            string.Equals(p.Name, settings.SelectedProfileName, StringComparison.OrdinalIgnoreCase));
        if (selected is { IsBuiltIn: false, Display.VirtualDisplay: true })
        {
            return With(settings, enabled: true, selected.Display.Mode);
        }

        // Nothing selected that asked for the virtual display. Someone who has been using Optima
        // chose that, and keeps it. A PC that is only now being set up gets the display when the
        // driver is there for it, and plays on its real screen when it is not: a launch must
        // never fail for a driver nobody installed.
        return With(settings, enabled: freshInstall && driverInstalled,
            new DisplayMode(settings.VirtualDisplayWidth, settings.VirtualDisplayHeight, settings.VirtualDisplayRefreshRate));
    }

    private static AppSettings With(AppSettings settings, bool enabled, DisplayMode mode) => settings with
    {
        VirtualDisplayEnabled = enabled,
        VirtualDisplayWidth = mode.Width,
        VirtualDisplayHeight = mode.Height,
        VirtualDisplayRefreshRate = mode.RefreshRate,
    };
}
