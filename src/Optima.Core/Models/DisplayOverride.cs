namespace Optima.Core.Models;

/// <summary>Cosmetic, Optima-only presentation overrides for one attached display: custom name, list position and visibility.</summary>
public sealed record DisplayOverride
{
    public string? CustomName { get; init; }

    public int? SortIndex { get; init; }

    public bool Hidden { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(CustomName) && SortIndex is null && !Hidden;
}

/// <summary>Merges the OS-reported display list with the user's cosmetic overrides (rename / reorder / hide).</summary>
public static class DisplayPresentation
{
    public static string OverrideKey(DisplayInfo display)
        => string.IsNullOrEmpty(display.DevicePath) ? display.DeviceName : display.DevicePath;

    public static string? CustomName(DisplayInfo display, IReadOnlyDictionary<string, DisplayOverride> overrides)
        => overrides.TryGetValue(OverrideKey(display), out var value) && !string.IsNullOrWhiteSpace(value.CustomName)
            ? value.CustomName
            : null;

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
