using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Optima.App.Effects;

namespace Optima.App.Controls;

/// <summary>
/// A glass strip that refracts the in-app content beneath it. It snapshots the inherited
/// <see cref="BackdropProperty"/> visual (the shell's ambient field, which the panel is not
/// part of) through a live VisualBrush rendered at a fraction of device resolution, optionally
/// blurs it, then runs <see cref="GlassEffect"/> for the shape mask, refraction, chroma and the
/// pointer-reactive specular. A <see cref="ChamferBorder"/> on top draws the rim lines and
/// hosts the child, clipped to the same shape.
/// </summary>
public sealed class GlassPanel : Grid
{
    private const double Bleed = 24;

    /// <summary>Inherited: set once on the shell root, every panel below refracts that visual.</summary>
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.RegisterAttached(
        "Backdrop", typeof(Visual), typeof(GlassPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnBackdropChanged));

    public static void SetBackdrop(DependencyObject element, Visual? value) => element.SetValue(BackdropProperty, value);
    public static Visual? GetBackdrop(DependencyObject element) => (Visual?)element.GetValue(BackdropProperty);

    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(UIElement), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._frame.Child = e.NewValue as UIElement));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(GlassPanel),
        new PropertyMetadata(new Thickness(16, 12, 16, 14), (d, e) => ((GlassPanel)d)._frame.Padding = (Thickness)e.NewValue));

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(10.0, (d, e) => ((GlassPanel)d).OnShapeChanged()));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(0.0, (d, e) => ((GlassPanel)d).OnShapeChanged()));

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(0.0, (d, e) => ((GlassPanel)d).OnBlurChanged()));

    public static readonly DependencyProperty DownsampleProperty = DependencyProperty.Register(
        nameof(Downsample), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(4.0, (d, e) => ((GlassPanel)d).OnBlurChanged()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(GlassPanel),
        new PropertyMetadata(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), (d, e) => ((GlassPanel)d).Glass.Tint = (Color)e.NewValue));

    public static readonly DependencyProperty TopLineProperty = DependencyProperty.Register(
        nameof(TopLine), typeof(Brush), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._frame.TopLine = e.NewValue as Brush));

    public static readonly DependencyProperty BottomLineProperty = DependencyProperty.Register(
        nameof(BottomLine), typeof(Brush), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._frame.BottomLine = e.NewValue as Brush));

    public static readonly DependencyProperty LeadingEdgeProperty = DependencyProperty.Register(
        nameof(LeadingEdge), typeof(Brush), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._frame.LeadingEdge = e.NewValue as Brush));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GlassPanel),
        new PropertyMetadata(null, (d, e) => ((GlassPanel)d)._frame.Background = e.NewValue as Brush));

    /// <summary>Every live panel, so the shell can move the light on all of them at once.</summary>
    private static readonly List<WeakReference<GlassPanel>> Live = [];

    private readonly VisualBrush _brush;
    private readonly Rectangle _backdrop;
    private readonly Grid _stack;
    private readonly ChamferBorder _frame;
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
        Blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        Glass = new GlassEffect { Inset = Bleed, Radius = CornerRadius, Chamfer = Chamfer, Tint = Tint, Edge = 20, Refract = 10, Chroma = 0.35, Specular = 0.9 };
        _backdrop = new Rectangle { Fill = _brush };
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.Linear);
        _stack = new Grid { Margin = new Thickness(-Bleed), Effect = Glass, IsHitTestVisible = false };
        _stack.Children.Add(_backdrop);
        _stack.SizeChanged += (_, _) => Glass.Size = new Point(_stack.ActualWidth, _stack.ActualHeight);
        _frame = new ChamferBorder { Chamfer = Chamfer, Padding = Padding };
        Children.Add(_stack);
        Children.Add(_frame);
        OnBlurChanged();
        LayoutUpdated += (_, _) => UpdateViewbox();
        Loaded += (_, _) => { Live.Add(new WeakReference<GlassPanel>(this)); _brush.Visual = GetBackdrop(this); };
        Unloaded += (_, _) => Live.RemoveAll(w => !w.TryGetTarget(out var p) || ReferenceEquals(p, this));
    }

    public UIElement? Child { get => (UIElement?)GetValue(ChildProperty); set => SetValue(ChildProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    /// <summary>Resolution divisor for the snapshot (4 = one sixteenth of the pixels). Minimum 1.</summary>
    public double Downsample { get => (double)GetValue(DownsampleProperty); set => SetValue(DownsampleProperty, value); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Brush? TopLine { get => (Brush?)GetValue(TopLineProperty); set => SetValue(TopLineProperty, value); }
    public Brush? BottomLine { get => (Brush?)GetValue(BottomLineProperty); set => SetValue(BottomLineProperty, value); }
    public Brush? LeadingEdge { get => (Brush?)GetValue(LeadingEdgeProperty); set => SetValue(LeadingEdgeProperty, value); }
    /// <summary>A flat fill drawn over the glass, for panels that need extra opacity (dialogs).</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>The glass pass; the Glass lab binds sliders to its inputs.</summary>
    public GlassEffect Glass { get; }

    public BlurEffect Blur { get; }

    /// <summary>Moves this panel's specular light. The point is in the panel's own coordinates.</summary>
    public void SetLight(Point panelPoint)
        => Glass.Light = new Point(panelPoint.X + Bleed, panelPoint.Y + Bleed);

    /// <summary>Moves the light on every live panel from a pointer position in root coordinates.</summary>
    public static void NotifyPointer(Visual root, Point rootPoint)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            if (!Live[i].TryGetTarget(out var panel) || !panel.IsVisible)
            {
                continue;
            }
            try
            {
                var local = root.TransformToDescendant(panel)?.Transform(rootPoint) ?? new Point(-1000, -1000);
                panel.SetLight(local);
            }
            catch (InvalidOperationException)
            {
                // Not in the same tree; leave the light where it is.
            }
        }
    }

    /// <summary>Puts every light out of reach (reduced motion, window not foreground).</summary>
    public static void ClearLights()
    {
        foreach (var weak in Live)
        {
            if (weak.TryGetTarget(out var panel))
            {
                panel.Glass.Light = new Point(-1000, -1000);
            }
        }
    }

    private static void OnBackdropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlassPanel panel)
        {
            panel._brush.Visual = e.NewValue as Visual;
        }
    }

    private void OnShapeChanged()
    {
        Glass.Chamfer = Chamfer;
        Glass.Radius = CornerRadius;
        _frame.Chamfer = Chamfer;
    }

    private void OnBlurChanged()
    {
        var scale = 1.0 / Math.Max(1.0, Downsample);
        _backdrop.CacheMode = new BitmapCache { RenderAtScale = scale, EnableClearType = false, SnapsToDevicePixels = false };
        if (BlurRadius > 0)
        {
            Blur.Radius = BlurRadius * scale;
            _backdrop.Effect = Blur;
        }
        else
        {
            _backdrop.Effect = null;
        }
    }

    /// <summary>Keeps the snapshot window aligned with where the panel sits over the source.</summary>
    private void UpdateViewbox()
    {
        var source = _brush.Visual;
        if (source is null || !IsVisible || _stack.ActualWidth <= 0)
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
