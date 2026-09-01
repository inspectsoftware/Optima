namespace Optima.Core.Theming;

/// <summary>
/// Whether the UI may move. By default Optima follows the Windows "animation effects"
/// switch; the user can untick that to keep Optima's motion regardless. Pure logic so it can
/// be tested without a window.
/// </summary>
public static class MotionPolicy
{
    public static bool IsEnabled(bool windowsAnimationsOn, bool followWindows)
        => !followWindows || windowsAnimationsOn;

    /// <summary>The duration to actually use: the design value when motion is on, zero when off.</summary>
    public static TimeSpan Duration(TimeSpan designed, bool enabled)
        => enabled ? designed : TimeSpan.Zero;
}
