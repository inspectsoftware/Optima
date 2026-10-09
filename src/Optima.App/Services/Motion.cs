using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Optima.Core.Theming;

namespace Optima.App.Services;

/// <summary>App-wide motion switch.</summary>
public static class Motion
{
    private static string _mode = MotionPolicy.System;
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

    public static bool Enabled => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _mode) && _foreground && !_gameRunning;

    public static bool Allowed => MotionPolicy.IsEnabled(SystemParameters.ClientAreaAnimation, _mode);

    public static TimeSpan Duration(int milliseconds)
        => MotionPolicy.Duration(TimeSpan.FromMilliseconds(milliseconds), Enabled);

    /// <summary>The length a move of <paramref name="milliseconds"/> has right now, in seconds: zero whenever motion is off.</summary>
    public static double Seconds(int milliseconds) => Enabled ? milliseconds / 1000.0 : 0;

    /// <summary>The one curve (<see cref="MotionSpec.EaseOut"/>), for anything animated by WPF itself.</summary>
    public static IEasingFunction Ease { get; } = new Curve(0);

    /// <summary>
    /// Brings an element in: it fades up and rises into place. <paramref name="order"/> is its place
    /// in a group that arrives together, each one a step after the one before it;
    /// <paramref name="from"/> is how far below its place it starts.
    /// </summary>
    public static void Rise(UIElement element, int order = 0, double from = 14)
    {
        var move = Seconds(MotionSpec.MoveMs);
        if (move <= 0)
        {
            return;
        }

        // The wait is part of the animation, not a begin time: until its begin time an animation
        // does not hold its start value, so the element would show for a moment and then vanish.
        var wait = Math.Min(order, 8) * MotionSpec.StaggerMs / 1000.0;
        var length = new Duration(TimeSpan.FromSeconds(wait + move));
        var ease = new Curve(wait / (wait + move));

        // From a start to whatever the element's own value is: a dimmed element ends dimmed.
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation { From = 0, Duration = length, EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } shift)
        {
            if (element.RenderTransform != Transform.Identity)
            {
                return;
            }
            element.RenderTransform = shift = new TranslateTransform();
        }
        var rise = new DoubleAnimation { From = from, Duration = length, EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        // Glass samples the backdrop at the place layout last gave it, and a transform is not layout.
        rise.Completed += (_, _) => Controls.GlassPanel.RefreshBackdrops();
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>The shared curve, after standing still for the first <paramref name="waitShare"/> of the time.</summary>
    private sealed class Curve(double waitShare) : IEasingFunction
    {
        public double Ease(double normalizedTime) => MotionSpec.EaseOut((normalizedTime - waitShare) / (1 - waitShare));
    }

    /// <summary>"System", "On" or "Off", from Settings (<see cref="MotionPolicy.Modes"/>).</summary>
    public static void SetMode(string mode)
    {
        if (string.Equals(_mode, mode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _mode = mode;
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
            // Raised on whichever UI thread hears Windows first, and while the splash is up that
            // can be the splash's. The listeners all belong to the main window's thread.
            if (Application.Current?.Dispatcher is { } main && !main.CheckAccess())
            {
                main.BeginInvoke(() => Changed?.Invoke());
                return;
            }
            Changed?.Invoke();
        }
    }
}
