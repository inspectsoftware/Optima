namespace Optima.Core.Models;

/// <summary>
/// Cosmetic, Optima-only presentation overrides for one attached display: custom name, list
/// position and visibility. Purely presentation; Windows display configuration is never touched.
/// </summary>
public sealed record DisplayOverride
{
    /// <summary>User-chosen name shown instead of the adapter/monitor description.</summary>
    public string? CustomName { get; init; }

    /// <summary>Position in the displays list; unset displays keep their enumeration order.</summary>
    public int? SortIndex { get; init; }

    /// <summary>Removed from the list until "show hidden" reveals it again.</summary>
    public bool Hidden { get; init; }

    /// <summary>An override carrying no information can be dropped from the settings file.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(CustomName) && SortIndex is null && !Hidden;
}

/// <summary>
/// Merges the OS-reported display list with the user's cosmetic overrides (rename / reorder /
/// hide). Overrides are keyed by the PnP device path because GDI device names can shift
/// between refreshes; the GDI name is the fallback key for displays without a path.
/// </summary>
public static class DisplayPresentation
{
    public static string OverrideKey(DisplayInfo display)
        => string.IsNullOrEmpty(display.DevicePath) ? display.DeviceName : display.DevicePath;

    /// <summary>The user's custom name for this display, or null when none is set.</summary>
    public static string? CustomName(DisplayInfo display, IReadOnlyDictionary<string, DisplayOverride> overrides)
        => overrides.TryGetValue(OverrideKey(display), out var value) && !string.IsNullOrWhiteSpace(value.CustomName)
            ? value.CustomName
            : null;

    /// <summary>Applies the hidden/inactive filters and the user's ordering to the OS display list.</summary>
    public static IReadOnlyList<DisplayInfo> Arrange(
        IReadOnlyList<DisplayInfo> displays,
        IReadOnlyDictionary<string, DisplayOverride> overrides,
        bool hideInactive,
        bool includeHidden = false)
        => displays
            .Select((display, index) => (Display: display, Override: overrides.GetValueOrDefault(OverrideKey(display)), Index: index))
            .Where(x => includeHidden || x.Override?.Hidden != true)
            .Where(x => !hideInactive || x.Display.IsActive)
            .OrderBy(x => x.Override?.SortIndex ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Display)
            .ToList();
}
