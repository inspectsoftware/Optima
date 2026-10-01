using System.Windows;
using System.Windows.Media.Animation;
using Optima.App.Services;

namespace Optima.App.Views;

/// <summary>
/// The launch splash: shown while the host builds, held for a minimum beat so it never
/// flashes, then faded out. With the motion switch off everything renders as a static
/// frame (mark fully visible, sweep parked) and the close is instant.
/// </summary>
public sealed partial class SplashWindow : Window
{
    private static readonly string[] StatusSteps =
    [
        "loading profiles",
        "reading settings",
        "checking the game",
        "starting monitors",
    ];

    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private readonly bool _animate;
    private int _statusIndex;
    private bool _closing;

    public SplashWindow()
    {
        InitializeComponent();
        _animate = Motion.Enabled;

        VersionText.Text = "v" + (typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusIndex = (_statusIndex + 1) % StatusSteps.Length;
            StatusText.Text = StatusSteps[_statusIndex];
        };

        Loaded += (_, _) =>
        {
            _statusTimer.Start();
            if (_animate)
            {
                var breathe = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                Mark.BeginAnimation(OpacityProperty, breathe);

                var run = new DoubleAnimation(-96, 280, TimeSpan.FromMilliseconds(1600))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                SweepTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, run);
            }
            else
            {
                Mark.Opacity = 1;
            }
        };
    }

    /// <summary>
    /// The handover: after the minimum display beat, the card expands to the main window's
    /// exact bounds and dissolves, revealing the app that was already sitting underneath.
    /// With motion off the splash simply closes; the minimum beat still applies.
    /// </summary>
    public async Task ExpandIntoAsync(Window target, TimeSpan minimumDisplay, CancellationToken ct = default)
    {
        var shown = DateTimeOffset.Now - ShownAt;
        var wait = minimumDisplay - shown;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(true);
        }

        _statusTimer.Stop();
        if (!_animate)
        {
            CloseNow();
            return;
        }

        // Park the breathing and the sweep so they do not fight the expansion.
        Mark.BeginAnimation(OpacityProperty, null);
        Mark.Opacity = 1;
        SweepTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);

        var bounds = target.WindowState == WindowState.Maximized
            ? new Rect(SystemParameters.WorkArea.Location, SystemParameters.WorkArea.Size)
            : new Rect(target.Left, target.Top, target.ActualWidth, target.ActualHeight);

        var duration = TimeSpan.FromMilliseconds(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // Grow the window itself; hold the final values until the close so nothing snaps back.
        Animate(LeftProperty, Left, bounds.Left, duration, ease);
        Animate(TopProperty, Top, bounds.Top, duration, ease);
        Animate(WidthProperty, Width, bounds.Width, duration, ease);
        Animate(HeightProperty, Height, bounds.Height, duration, ease);

        // Dissolve while expanding, so the card reads as melting into the app underneath.
        var fade = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) => CloseNow();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Animate(DependencyProperty property, double from, double to, Duration duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(property, animation);
    }

    private DateTimeOffset ShownAt { get; } = DateTimeOffset.Now;

    private void CloseNow()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        Close();
    }
}
