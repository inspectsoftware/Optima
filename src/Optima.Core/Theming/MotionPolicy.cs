namespace Optima.Core.Theming;

/// <summary>Whether the UI may move.</summary>
public static class MotionPolicy
{
    /// <summary>Do what Windows says ("Show animations in Windows"). The default.</summary>
    public const string System = "System";

    /// <summary>Move regardless of the Windows setting.</summary>
    public const string On = "On";

    /// <summary>Everything arrives at once.</summary>
    public const string Off = "Off";

    public static IReadOnlyList<string> Modes { get; } = [System, On, Off];

    /// <param name="mode">One of <see cref="Modes"/>; anything else is read as <see cref="System"/>.</param>
    public static bool IsEnabled(bool windowsAnimationsOn, string? mode)
        => string.Equals(mode, On, StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase) && windowsAnimationsOn);

    public static TimeSpan Duration(TimeSpan designed, bool enabled)
        => enabled ? designed : TimeSpan.Zero;
}
