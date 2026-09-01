using System.ComponentModel;
using System.Windows;
using Optima.Core.Theming;

namespace Optima.App.Services;

/// <summary>
/// App-wide motion switch. Combines the Windows animation setting (via
/// <see cref="MotionPolicy"/>), the user's "follow Windows" preference and whether the
/// window is foreground. Everything that moves asks <see cref="Enabled"/> and listens to
/// <see cref="Changed"/>; when it is false, drift and light stop and transitions are instant.
/// </summary>
public static class Motion
{
    private static bool _followWindows = true;
    private static bool _foreground = true;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public static event Action? Changed;

    /// <summary>True when transitions, drift and the pointer light may run right now.</summary>
    public static bool Enabled => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _followWindows) && _foreground;

    /// <summary>True when the user allows motion at all, regardless of foreground state.</summary>
    public static bool Allowed => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _followWindows);

    public static TimeSpan Duration(int milliseconds)
        => MotionPolicy.Duration(TimeSpan.FromMilliseconds(milliseconds), Enabled);

    public static void SetFollowWindows(bool follow)
    {
        if (_followWindows == follow)
        {
            return;
        }
        _followWindows = follow;
        Changed?.Invoke();
    }

    public static void SetForeground(bool foreground)
    {
        if (_foreground == foreground)
        {
            return;
        }
        _foreground = foreground;
        Changed?.Invoke();
    }

    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemParameters.ClientAreaAnimation))
        {
            Changed?.Invoke();
        }
    }
}
