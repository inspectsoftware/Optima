using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Optima.App.Services;
using Optima.Core.Theming;

namespace Optima.App.Controls;

/// <summary>
/// The BOOST master switch: a round dial whose ring comes apart into six segments, spins up and
/// locks back into a violet-to-green ring that reads ENABLED. Everything is drawn here on a
/// 320-unit square and scaled to the control; <see cref="BoostDialTimeline"/> decides what each
/// moment looks like. The run only plays for a click, never for a value that arrives from settings,
/// and not at all while motion is off (the game is running, or Windows animations are disabled).
/// </summary>
public sealed class BoostDial : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(BoostDial),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsOnChanged));

    public static readonly DependencyProperty FeatureCountProperty = DependencyProperty.Register(
        nameof(FeatureCount), typeof(int), typeof(BoostDial),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(BoostDial), new PropertyMetadata(null));

    private const double Unit = 320;
    private static readonly Point Center = new(160, 160);
    private static readonly Color Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color LabelViolet = Color.FromRgb(0xA7, 0x8B, 0xFA);
    private static readonly TimeSpan RestingFrame = TimeSpan.FromMilliseconds(50);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private BoostDialMode _mode;
    private double _modeStartedMs;
    private TimeSpan _lastRestingFrame;
    private bool _animateNextChange;
    private bool _hooked;

    public BoostDial()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        Loaded += (_, _) =>
        {
            Motion.Changed -= Hook;
            Motion.Changed += Hook;
            Hook();
        };
        Unloaded += (_, _) =>
        {
            Motion.Changed -= Hook;
            Unhook();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Hook();
            }
            else
            {
                Unhook();
            }
        };
    }

    /// <summary>Whether Boost is on. Bound one way from the view model; a click asks <see cref="Command"/> to change it.</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    /// <summary>How many features the switch arms; counted up in the centre during the run.</summary>
    public int FeatureCount
    {
        get => (int)GetValue(FeatureCountProperty);
        set => SetValue(FeatureCountProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (BoostDial)d;
        var on = (bool)e.NewValue;
        // Only a hooked dial plays the run: nothing else would advance it, and a click is ignored
        // for as long as one is in flight.
        if (dial._animateNextChange && dial._hooked && Motion.Enabled)
        {
            dial._mode = on ? BoostDialMode.Enabling : BoostDialMode.Disabling;
            dial._modeStartedMs = dial._clock.Elapsed.TotalMilliseconds;
        }
        else
        {
            dial._mode = on ? BoostDialMode.On : BoostDialMode.Off;
        }
        dial._animateNextChange = false;
        dial.InvalidateVisual();
    }

    /// <summary>
    /// Hooked only while motion is on. A subscriber keeps the frame tick running, and with BOOST
    /// open the dial was the one thing ticking every frame of a window that was unfocused,
    /// minimized or behind a game.
    /// </summary>
    private void Hook()
    {
        if (!_hooked && IsLoaded && IsVisible && Motion.Enabled)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    private void Unhook()
    {
        if (!_hooked)
        {
            return;
        }
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
        // The hook only comes back with motion on, so a run in flight ends where it was going
        // instead of waiting for a frame that may never come.
        if (_mode is BoostDialMode.Enabling or BoostDialMode.Disabling)
        {
            _mode = _mode == BoostDialMode.Enabling ? BoostDialMode.On : BoostDialMode.Off;
            InvalidateVisual();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var running = _mode is BoostDialMode.Enabling or BoostDialMode.Disabling;
        if (running)
        {
            var t = _clock.Elapsed.TotalMilliseconds - _modeStartedMs;
            var length = _mode == BoostDialMode.Enabling ? BoostDialTimeline.EnableMs : BoostDialTimeline.DisableMs;
            if (t >= length || !Motion.Enabled)
            {
                _mode = _mode == BoostDialMode.Enabling ? BoostDialMode.On : BoostDialMode.Off;
            }
            InvalidateVisual();
            return;
        }

        // With motion off nothing here moves: one still frame, and the frame tick is let go until
        // motion is back.
        if (!Motion.Enabled)
        {
            Unhook();
            InvalidateVisual();
            return;
        }

        // At rest only the glow breathes and the light sweeps; a fraction of the frames is plenty.
        if (_clock.Elapsed - _lastRestingFrame < RestingFrame)
        {
            return;
        }
        _lastRestingFrame = _clock.Elapsed;
        InvalidateVisual();
    }

    private void Toggle()
    {
        if (_mode is BoostDialMode.Enabling or BoostDialMode.Disabling)
        {
            return;
        }
        if (Command is { } command && command.CanExecute(null))
        {
            _animateNextChange = true;
            command.Execute(null);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Toggle();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter)
        {
            Toggle();
            e.Handled = true;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var nowMs = _clock.Elapsed.TotalMilliseconds;
        var frame = BoostDialTimeline.At(_mode, nowMs - _modeStartedMs);
        var moving = Motion.Enabled;
        var breath = moving ? 0.5 + 0.5 * Math.Sin(nowMs / 1300) : 0.5;
        var gold = 1 - frame.Gradient;
        var accent = (TryFindResource("Brush.Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0xE8, 0xB4, 0x5A);
        var text = TryFindResource("Brush.Text") as Brush ?? Brushes.White;
        var muted = TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray;
        var surface = TryFindResource("Brush.Surface") as Brush ?? Brushes.Black;
        var hairline = (TryFindResource("Brush.HairlineStrong") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3A, 0x3A, 0x3A);

        var scale = side / Unit;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - side) / 2, (ActualHeight - side) / 2));

        // The whole square takes the click, including the gaps between detached segments.
        dc.DrawEllipse(Brushes.Transparent, null, Center, 152, 152);

        // Glow: a few wide, faint strokes stand in for a blur, which would cost a bitmap effect per frame.
        var goldGlow = gold * (0.10 + 0.08 * breath + (IsMouseOver ? 0.12 : 0) + 0.1 * Math.Max(0, frame.Detach));
        var gradGlow = frame.Gradient * (0.34 + 0.16 * breath);
        foreach (var (width, share) in new[] { (22.0, 0.50), (36.0, 0.30), (52.0, 0.20) })
        {
            if (goldGlow > 0.004)
            {
                dc.DrawEllipse(null, new Pen(Solid(accent, goldGlow * share), width), Center, 114, 114);
            }
            if (gradGlow > 0.004)
            {
                dc.DrawEllipse(null, new Pen(Gradient(gradGlow * share), width), Center, 114, 114);
            }
        }

        // Disc, tick track, progress.
        var disc = 1 - 0.03 * frame.Dip;
        dc.DrawEllipse(surface, new Pen(Solid(IsMouseOver ? Lighten(hairline) : hairline, 1), 1), Center, 96 * disc, 96 * disc);
        dc.DrawEllipse(null, new Pen(Solid(hairline, 0.75), 5) { DashStyle = new DashStyle([0.24, 1.5612], 0) }, Center, 86 * disc, 86 * disc);
        dc.DrawEllipse(null, new Pen(Solid(hairline, 0.5), 2), Center, 76 * disc, 76 * disc);
        if (frame.Progress > 0.001)
        {
            DrawArc(dc, 76 * disc, -90, 359.9 * frame.Progress, 2, accent, gold, frame.Gradient, round: true);
        }

        // The ring: two sets of three segments. One set leaves outward, the other inward.
        var outerRadius = 118 * (1 + 0.16 * frame.Detach);
        var innerRadius = 118 * (1 - 0.075 * frame.Detach);
        var thickness = 14 - 4 * frame.Detach;
        for (var k = 0; k < 3; k++)
        {
            DrawArc(dc, outerRadius, frame.OuterRotation - 87 + 120 * k, 54, thickness, accent, gold, frame.Gradient, round: false);
            DrawArc(dc, innerRadius, frame.InnerRotation - 27 + 120 * k, 54, thickness, accent, gold, frame.Gradient, round: false);
        }

        if (_mode == BoostDialMode.On && moving)
        {
            var sweep = nowMs / 6000 * 360 % 360;
            dc.DrawGeometry(null, new Pen(Solid(Colors.White, 0.3), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(118, sweep, 14));
        }
        if (frame.PulseOpacity > 0.004)
        {
            dc.DrawEllipse(null, new Pen(Gradient(frame.PulseOpacity), 3), Center, 118 * frame.PulseScale, 118 * frame.PulseScale);
        }
        if (IsKeyboardFocused)
        {
            dc.DrawEllipse(null, new Pen(text, 2) { DashStyle = new DashStyle([2, 3], 0) }, Center, 152, 152);
        }

        DrawLabel(dc, frame, text, muted);
        dc.Pop();
    }

    private void DrawLabel(DrawingContext dc, BoostDialFrame frame, Brush text, Brush muted)
    {
        if (frame.LabelOpacity <= 0.004)
        {
            return;
        }
        var body = TryFindResource("Font.Body") as FontFamily ?? new FontFamily("Segoe UI");
        var mono = TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var total = Math.Max(1, FeatureCount);

        var (title, titleSize, weight, sub) = frame.Label switch
        {
            BoostDialLabel.Arming => ("ARMING", 25.0, FontWeights.SemiBold,
                $"{Math.Round(frame.Progress * total).ToString(CultureInfo.InvariantCulture)} / {total.ToString(CultureInfo.InvariantCulture)} features"),
            BoostDialLabel.Enabled => ("ENABLED", 27.0, FontWeights.Bold, "click to disable"),
            _ => ("BOOST", 31.0, FontWeights.Bold, "click to enable"),
        };

        var titleText = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(body, FontStyles.Normal, weight, FontStretches.Normal), titleSize, text, dpi);
        var subText = new FormattedText(sub, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12.5, muted, dpi);

        const double gap = 7;
        var top = Center.Y - (titleText.Height + gap + subText.Height) / 2;
        var titleOrigin = new Point(Center.X - titleText.Width / 2, top);

        dc.PushOpacity(frame.LabelOpacity);
        if (frame.Label == BoostDialLabel.Enabled)
        {
            // Drawn as a shape so the gradient runs across the whole word, not per letter.
            var word = new LinearGradientBrush(LabelViolet, Green, new Point(0, 0.5), new Point(1, 0.5));
            dc.DrawGeometry(word, null, titleText.BuildGeometry(titleOrigin));
        }
        else
        {
            dc.DrawText(titleText, titleOrigin);
        }
        dc.DrawText(subText, new Point(Center.X - subText.Width / 2, top + titleText.Height + gap));
        dc.Pop();
    }

    private static void DrawArc(
        DrawingContext dc, double radius, double startDegrees, double sweepDegrees, double thickness,
        Color accent, double gold, double gradient, bool round)
    {
        var arc = Arc(radius, startDegrees, sweepDegrees);
        var cap = round ? PenLineCap.Round : PenLineCap.Flat;
        if (gold > 0.004)
        {
            dc.DrawGeometry(null, new Pen(Solid(accent, gold), thickness) { StartLineCap = cap, EndLineCap = cap }, arc);
        }
        if (gradient > 0.004)
        {
            dc.DrawGeometry(null, new Pen(Gradient(gradient), thickness) { StartLineCap = cap, EndLineCap = cap }, arc);
        }
    }

    /// <summary>Clockwise arc around the centre; 0 degrees is three o'clock.</summary>
    private static StreamGeometry Arc(double radius, double startDegrees, double sweepDegrees)
    {
        var start = startDegrees * Math.PI / 180;
        var end = (startDegrees + sweepDegrees) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(
                new Point(Center.X + radius * Math.Cos(start), Center.Y + radius * Math.Sin(start)), isFilled: false, isClosed: false);
            context.ArcTo(
                new Point(Center.X + radius * Math.Cos(end), Center.Y + radius * Math.Sin(end)),
                new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Solid(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        return brush;
    }

    /// <summary>The violet-to-green of the enabled ring, fixed in space so the segments turn through it.</summary>
    private static LinearGradientBrush Gradient(double opacity)
    {
        var brush = new LinearGradientBrush(Violet, Green, new Point(52, 60), new Point(268, 260))
        {
            MappingMode = BrushMappingMode.Absolute,
            Opacity = Math.Clamp(opacity, 0, 1),
        };
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color color)
        => Color.FromRgb((byte)Math.Min(255, color.R + 48), (byte)Math.Min(255, color.G + 48), (byte)Math.Min(255, color.B + 48));
}
