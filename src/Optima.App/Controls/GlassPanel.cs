using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Optima.App.Effects;

namespace Optima.App.Controls;

/// <summary>
/// A glass panel that refracts the in-app content beneath it. It snapshots
/// <see cref="BackdropSource"/> (a visual the panel is NOT part of) through a live
/// VisualBrush, blurs it with the built-in hardware BlurEffect, then runs
/// <see cref="GlassEffect"/> for the rounded mask, refraction, chroma and specular.
/// The blur is rendered with a bleed margin so the rim never samples transparent pixels.
/// </summary>
public sealed class GlassPanel : Grid
{
    private const double Bleed = 40;

    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(Visual), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._brush.Visual = e.NewValue as Visual));

    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(UIElement), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d).OnChildChanged(e.OldValue as UIElement, e.NewValue as UIElement)));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(16.0, (d, e) => ((GlassPanel)d).Glass.Radius = (double)e.NewValue));

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(28.0, (d, e) => ((GlassPanel)d).Blur.Radius = (double)e.NewValue / ((GlassPanel)d).Downsample));

    public static readonly DependencyProperty DownsampleProperty = DependencyProperty.Register(
        nameof(Downsample), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(2.0, (d, e) => ((GlassPanel)d).OnDownsampleChanged((double)e.NewValue)));

    private readonly VisualBrush _brush;
    private readonly Rectangle _backdrop;
    private readonly Grid _stack;
    private Visual? _lastSource;

    public GlassPanel()
    {
        _brush = new VisualBrush
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        Blur = new BlurEffect { Radius = BlurRadius / Downsample, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        Glass = new GlassEffect { Inset = Bleed, Radius = CornerRadius };
        // The snapshot and its blur are rendered at a fraction of device resolution: blur is
        // the expensive pass and a blurred image loses nothing when upscaled. The glass pass
        // that follows still runs at full resolution, so rims and highlights stay crisp.
        _backdrop = new Rectangle
        {
            Fill = _brush,
            Effect = Blur,
            CacheMode = new BitmapCache { RenderAtScale = 1.0 / Downsample, EnableClearType = false, SnapsToDevicePixels = false },
        };
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.Linear);
        _stack = new Grid { Margin = new Thickness(-Bleed), Effect = Glass, IsHitTestVisible = false };
        _stack.Children.Add(_backdrop);
        _stack.SizeChanged += (_, _) => Glass.Size = new Point(_stack.ActualWidth, _stack.ActualHeight);
        Children.Add(_stack);
        LayoutUpdated += (_, _) => UpdateViewbox();
    }

    /// <summary>The visual the panel refracts. Must not contain the panel.</summary>
    public Visual? BackdropSource
    {
        get => (Visual?)GetValue(BackdropSourceProperty);
        set => SetValue(BackdropSourceProperty, value);
    }

    public UIElement? Child
    {
        get => (UIElement?)GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    /// <summary>Resolution divisor for the snapshot and blur (2 = quarter of the pixels). Minimum 1.</summary>
    public double Downsample
    {
        get => (double)GetValue(DownsampleProperty);
        set => SetValue(DownsampleProperty, value);
    }

    private void OnDownsampleChanged(double value)
    {
        var scale = 1.0 / Math.Max(1.0, value);
        _backdrop.CacheMode = new BitmapCache { RenderAtScale = scale, EnableClearType = false, SnapsToDevicePixels = false };
        Blur.Radius = BlurRadius * scale;
    }

    /// <summary>The glass pass; bind sliders to its Refract / Chroma / Specular / Tint.</summary>
    public GlassEffect Glass { get; }

    public BlurEffect Blur { get; }

    /// <summary>Moves the specular light. The point is in this panel's coordinates.</summary>
    public void SetLight(Point panelPoint)
        => Glass.Light = new Point(panelPoint.X + Bleed, panelPoint.Y + Bleed);

    private void OnChildChanged(UIElement? oldChild, UIElement? newChild)
    {
        if (oldChild is not null)
        {
            Children.Remove(oldChild);
        }
        if (newChild is not null)
        {
            Children.Add(newChild);
        }
    }

    /// <summary>Keeps the snapshot window aligned with where the panel sits over the source.</summary>
    private void UpdateViewbox()
    {
        if (BackdropSource is not { } source || !IsVisible || _stack.ActualWidth <= 0)
        {
            return;
        }
        Point origin;
        try
        {
            origin = _stack.TransformToVisual(source).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            // Not in the same visual tree yet.
            return;
        }
        var box = new Rect(origin.X, origin.Y, _stack.ActualWidth, _stack.ActualHeight);
        if (_brush.Viewbox != box || !ReferenceEquals(_lastSource, source))
        {
            _brush.Viewbox = box;
            _lastSource = source;
        }
    }
}
