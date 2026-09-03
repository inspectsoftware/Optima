using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Optima.App.Services;

namespace Optima.App.Controls;

/// <summary>Shell mood, driven by the launch state.</summary>
public enum AmbientState
{
    Rest,
    Session,
    Attention,
}

/// <summary>
/// The in-app ambient field under everything: a diagonal band of the accent, a cool body in the opposite corner, a HUD
/// grid fading toward the edges, and a red wash for attention.
/// </summary>
public sealed class AmbientField : FrameworkElement
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(AmbientField),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarmthProperty = DependencyProperty.Register(
        nameof(Warmth), typeof(double), typeof(AmbientField),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AlarmProperty = DependencyProperty.Register(
        nameof(Alarm), typeof(double), typeof(AmbientField),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromRgb(0xE8, 0xB4, 0x5A), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CoolProperty = DependencyProperty.Register(
        nameof(Cool), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8F, 0xA8, 0xCC), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridColorProperty = DependencyProperty.Register(
        nameof(GridColor), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private AnimationClock? _drift;
    private AmbientState _state;

    public AmbientField()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { Motion.Changed += OnMotionChanged; OnMotionChanged(); };
        Unloaded += (_, _) => { Motion.Changed -= OnMotionChanged; StopDrift(); };
    }

    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public double Warmth { get => (double)GetValue(WarmthProperty); set => SetValue(WarmthProperty, value); }
    public double Alarm { get => (double)GetValue(AlarmProperty); set => SetValue(AlarmProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Cool { get => (Color)GetValue(CoolProperty); set => SetValue(CoolProperty, value); }
    public Color GridColor { get => (Color)GetValue(GridColorProperty); set => SetValue(GridColorProperty, value); }

    public AmbientState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }
            _state = value;
            var warmth = value == AmbientState.Session ? 1.0 : 0.0;
            var alarm = value == AmbientState.Attention ? 1.0 : 0.0;
            var duration = Motion.Enabled ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
            BeginAnimation(WarmthProperty, new DoubleAnimation(warmth, duration) { EasingFunction = new SineEase() });
            BeginAnimation(AlarmProperty, new DoubleAnimation(alarm, duration) { EasingFunction = new SineEase() });
        }
    }

    private void OnMotionChanged()
    {
        if (Motion.Enabled)
        {
            if (_drift is null)
            {
                var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(10)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(animation, 15);
                _drift = animation.CreateClock();
                ApplyAnimationClock(PhaseProperty, _drift);
            }
            else
            {
                _drift.Controller?.Resume();
            }
        }
        else
        {
            _drift?.Controller?.Pause();
        }
    }

    private void StopDrift()
    {
        _drift?.Controller?.Stop();
        _drift = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        var angle = Phase * Math.PI * 2;
        var sx = Math.Sin(angle);
        var cy = Math.Cos(angle);
        var warmth = Math.Clamp(Warmth, 0, 1);
        var alarm = Math.Clamp(Alarm, 0, 1);

        var bandAlpha = 0.20 + 0.14 * warmth;
        var bandY = h * (0.32 - 0.10 * warmth) + cy * 18;
        var band = new RadialGradientBrush(
            Color.FromArgb((byte)(bandAlpha * 255), Accent.R, Accent.G, Accent.B),
            Color.FromArgb(0, Accent.R, Accent.G, Accent.B))
        {
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        band.Freeze();
        dc.PushTransform(new RotateTransform(-14, w * 0.5, bandY));
        dc.DrawEllipse(band, null, new Point(w * 0.5 + sx * 60, bandY), w * 0.75, 150 + 30 * warmth);
        dc.Pop();

        var cool = new RadialGradientBrush(
            Color.FromArgb(0x24, Cool.R, Cool.G, Cool.B),
            Color.FromArgb(0, Cool.R, Cool.G, Cool.B));
        cool.Freeze();
        dc.DrawEllipse(cool, null, new Point(w * 0.92 - sx * 40, h * 0.98 + cy * 24), 360, 260);

        if (alarm > 0.005)
        {
            var wash = new RadialGradientBrush(
                Color.FromArgb((byte)(0x3C * alarm), 0xE0, 0x5A, 0x5A),
                Color.FromArgb(0, 0xE0, 0x5A, 0x5A));
            wash.Freeze();
            dc.DrawEllipse(wash, null, new Point(w * 0.5, h * 0.5), w * 0.7, h * 0.7);
        }

        if (DrawGrid)
        {
            var grid = BuildGrid(GridColor);
            var mask = new RadialGradientBrush(Colors.Black, Colors.Transparent)
            {
                Center = new Point(0.5, 0.4),
                GradientOrigin = new Point(0.5, 0.4),
                RadiusX = 0.75,
                RadiusY = 0.75,
            };
            mask.Freeze();
            dc.PushOpacityMask(mask);
            dc.DrawRectangle(grid, null, new Rect(0, 0, w, h));
            dc.Pop();
        }
    }

    public static readonly DependencyProperty DrawGridProperty = DependencyProperty.Register(
        nameof(DrawGrid), typeof(bool), typeof(AmbientField),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool DrawGrid { get => (bool)GetValue(DrawGridProperty); set => SetValue(DrawGridProperty, value); }

    private static DrawingBrush? _gridCache;
    private static Color _gridCacheColor;

    private static DrawingBrush BuildGrid(Color color)
    {
        if (_gridCache is not null && _gridCacheColor == color)
        {
            return _gridCache;
        }
        var lines = new GeometryGroup();
        lines.Children.Add(new RectangleGeometry(new Rect(0, 0, 48, 1)));
        lines.Children.Add(new RectangleGeometry(new Rect(0, 0, 1, 48)));
        var brush = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(color), null, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 48, 48),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 48, 48),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        _gridCache = brush;
        _gridCacheColor = color;
        return brush;
    }
}
