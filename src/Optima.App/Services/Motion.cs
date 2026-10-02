using System.ComponentModel;
using System.Windows;
using Optima.Core.Theming;

namespace Optima.App.Services;

/// <summary>App-wide motion switch.</summary>
public static class Motion
{
    private static bool _followWindows = true;
    private static bool _foreground = true;
    private static bool _gameRunning;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public static event Action? Changed;

    /// <summary>
    /// False below render tier 2. Everything decorative in the shell is a blur, a pixel shader or a
    /// continuously animated backdrop; tiers 0 and 1 have no usable pixel shader hardware (tier 1
    /// runs them in software), where those cost more than the app itself, so the shell drops them.
    /// </summary>
    public static bool EffectsAvailable { get; } = (System.Windows.Media.RenderCapability.Tier >> 16) >= 2;

    /// <summary>
    /// True from the moment a session is starting until it ends. The shell keeps a full-window
    /// backdrop drifting and its panels lighting up under the pointer whenever this window is visible
    /// and focused — and during a session the launcher is either behind the game or on a second
    /// monitor, so that decoration is pure cost. The decorative layers stop; nothing functional does.
    /// </summary>
    public static bool Suspended => _gameRunning;

    public static bool Enabled => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _followWindows) && _foreground && !_gameRunning;

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

    /// <summary>Called on the session edges: decoration is off while a game runs.</summary>
    public static void SetGameRunning(bool running)
    {
        if (_gameRunning == running)
        {
            return;
        }
        _gameRunning = running;
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
