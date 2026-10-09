using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Optima.App.Services;
using Optima.Core.Theming;

namespace Optima.App.Controls;

/// <summary>Shell mood, driven by the launch state.</summary>
public enum AmbientState
{
    Rest,
    Session,
    Attention,
}

/// <summary>
/// The in-app ambient field under everything: three pools of light tinted from the accent, in an
/// arrangement of their own for every page (<see cref="Pools"/>). Going to another page, and a
/// launch starting, succeeding or failing, pours them to the next arrangement. In between nothing
/// moves and nothing is drawn again: every glass panel above re-runs its shader whenever this
/// changes, so a backdrop that drifts all the time is paid for by the whole window.
/// </summary>
public sealed class AmbientField : FrameworkElement
{
    /// <summary>How far the pour in progress has come, 0 to 1. At rest it is 1.</summary>
    private static readonly DependencyProperty PouredProperty = DependencyProperty.Register(
        "Poured", typeof(double), typeof(AmbientField),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromRgb(0xE8, 0xB4, 0x5A), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CoolProperty = DependencyProperty.Register(
        nameof(Cool), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8F, 0xA8, 0xCC), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridColorProperty = DependencyProperty.Register(
        nameof(GridColor), typeof(Color), typeof(AmbientField),
        new FrameworkPropertyMetadata(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    private Pool[] _from = Pools.For("");
    private Pool[] _to = Pools.For("");
    private string _page = "";
    private AmbientState _state;

    public AmbientField()
    {
        IsHitTestVisible = false;
    }

    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Color Cool { get => (Color)GetValue(CoolProperty); set => SetValue(CoolProperty, value); }
    public Color GridColor { get => (Color)GetValue(GridColorProperty); set => SetValue(GridColorProperty, value); }

    public AmbientState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                Send();
            }
        }
    }

    /// <summary>Pours the backdrop to the arrangement of this page.</summary>
    public void Pour(string page)
    {
        if (_page != page)
        {
            _page = page;
            Send();
        }
    }

    private void Send()
    {
        var rest = Pools.For(_page);
        _from = Now();
        _to = _state switch
        {
            AmbientState.Session => Pools.Warm(rest),
            AmbientState.Attention => Pools.Alarmed(rest),
            _ => rest,
        };

        // Before the window is up there is nobody to watch a pour, and start has other work to do.
        // Without a GPU the glass above would be redrawn on the CPU for every frame of it.
        var seconds = IsLoaded && Motion.EffectsAvailable ? Motion.Seconds(MotionSpec.AmbientMs) : 0;
        if (seconds <= 0)
        {
            BeginAnimation(PouredProperty, null);
            InvalidateVisual();
            return;
        }

        // The curve is in Pools.At, pool by pool; here time just runs. Thirty frames a second:
        // each one re-runs the shader of every glass panel on the page.
        var pour = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds));
        Timeline.SetDesiredFrameRate(pour, 30);
        BeginAnimation(PouredProperty, pour);
    }

    /// <summary>Where the pools are at this moment: what a pour that is redirected sets off from.</summary>
    private Pool[] Now()
    {
        var poured = (double)GetValue(PouredProperty);
        var now = new Pool[_to.Length];
        for (var i = 0; i < now.Length; i++)
        {
            now[i] = Pools.At(_from[i], _to[i], poured, i);
        }
        return now;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        static uint Argb(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        var pools = Now();
        for (var i = 0; i < pools.Length; i++)
        {
            var pool = pools[i];
            var argb = Pools.Colour(pool, Argb(Accent), Argb(Cool));
            var colour = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var brush = new RadialGradientBrush(colour, Color.FromArgb(0, colour.R, colour.G, colour.B));
            brush.Freeze();
            var centre = new Point(w * pool.X, h * pool.Y);

            // The first pool lies at a slant, as the accent band always has.
            if (i == 0)
            {
                dc.PushTransform(new RotateTransform(-14, centre.X, centre.Y));
            }
            dc.DrawEllipse(brush, null, centre, w * pool.Rx, h * pool.Ry);
            if (i == 0)
            {
                dc.Pop();
            }
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
