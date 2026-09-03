using System.ComponentModel;
using System.Windows;
using Optima.Core.Theming;

namespace Optima.App.Services;

/// <summary>App-wide motion switch.</summary>
public static class Motion
{
    private static bool _followWindows = true;
    private static bool _foreground = true;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public static event Action? Changed;

    public static bool Enabled => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _followWindows) && _foreground;

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
