namespace Optima.Core.Theming;

/// <summary>Whether the UI may move.</summary>
public static class MotionPolicy
{
    public static bool IsEnabled(bool windowsAnimationsOn, bool followWindows)
        => !followWindows || windowsAnimationsOn;

    public static TimeSpan Duration(TimeSpan designed, bool enabled)
        => enabled ? designed : TimeSpan.Zero;
}
