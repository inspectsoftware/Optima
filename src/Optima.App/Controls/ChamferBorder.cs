using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>
/// The HUD vessel: a rectangle with its top-left and bottom-right corners cut at 45 degrees.
/// Draws a fill, an optional 1 px outline, a specular line along the top edge, a dark line
/// along the bottom edge and a 2 px leading edge on the left, then clips its child to the
/// same shape. Replaces rounded Borders everywhere the HUD language applies.
/// </summary>
public class ChamferBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TopLineProperty = DependencyProperty.Register(
        nameof(TopLine), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BottomLineProperty = DependencyProperty.Register(
        nameof(BottomLine), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LeadingEdgeProperty = DependencyProperty.Register(
        nameof(LeadingEdge), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(Brush), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness), typeof(double), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClipChildProperty = DependencyProperty.Register(
        nameof(ClipChild), typeof(bool), typeof(ChamferBorder),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public Brush? TopLine { get => (Brush?)GetValue(TopLineProperty); set => SetValue(TopLineProperty, value); }
    public Brush? BottomLine { get => (Brush?)GetValue(BottomLineProperty); set => SetValue(BottomLineProperty, value); }
    public Brush? LeadingEdge { get => (Brush?)GetValue(LeadingEdgeProperty); set => SetValue(LeadingEdgeProperty, value); }
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public double OutlineThickness { get => (double)GetValue(OutlineThicknessProperty); set => SetValue(OutlineThicknessProperty, value); }
    public bool ClipChild { get => (bool)GetValue(ClipChildProperty); set => SetValue(ClipChildProperty, value); }

    public ChamferBorder()
    {
        SnapsToDevicePixels = true;
    }

    /// <summary>The chamfered outline for a given size, shared by the fill, the clip and the lines.</summary>
    public static Geometry BuildGeometry(Size size, double chamfer)
    {
        var w = Math.Max(0, size.Width);
        var h = Math.Max(0, size.Height);
        var c = Math.Max(0, Math.Min(chamfer, Math.Min(w, h) / 2));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(c, 0), true, true);
            ctx.LineTo(new Point(w, 0), true, false);
            ctx.LineTo(new Point(w, h - c), true, false);
            ctx.LineTo(new Point(w - c, h), true, false);
            ctx.LineTo(new Point(0, h), true, false);
            ctx.LineTo(new Point(0, c), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var pad = Padding;
        var inner = new Size(
            Math.Max(0, constraint.Width - pad.Left - pad.Right),
            Math.Max(0, constraint.Height - pad.Top - pad.Bottom));
        if (Child is null)
        {
            return new Size(pad.Left + pad.Right, pad.Top + pad.Bottom);
        }
        Child.Measure(inner);
        return new Size(
            Child.DesiredSize.Width + pad.Left + pad.Right,
            Child.DesiredSize.Height + pad.Top + pad.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var pad = Padding;
        Child?.Arrange(new Rect(
            pad.Left, pad.Top,
            Math.Max(0, arrangeSize.Width - pad.Left - pad.Right),
            Math.Max(0, arrangeSize.Height - pad.Top - pad.Bottom)));
        if (ClipChild && Child is not null)
        {
            Child.Clip = BuildChildClip(arrangeSize, pad);
        }
        return arrangeSize;
    }

    /// <summary>Clip in the child's coordinate space (offset by the padding).</summary>
    private Geometry BuildChildClip(Size size, Thickness pad)
    {
        var shape = BuildGeometry(size, Chamfer).Clone();
        shape.Transform = new TranslateTransform(-pad.Left, -pad.Top);
        shape.Freeze();
        return shape;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        var c = Math.Max(0, Math.Min(Chamfer, Math.Min(size.Width, size.Height) / 2));
        var shape = BuildGeometry(size, c);
        var pen = Outline is null || OutlineThickness <= 0 ? null : new Pen(Outline, OutlineThickness);
        if (Background is not null || pen is not null)
        {
            dc.DrawGeometry(Background, pen, shape);
        }
        if (TopLine is not null)
        {
            dc.DrawLine(new Pen(TopLine, 1), new Point(c, 0.5), new Point(size.Width, 0.5));
            dc.DrawLine(new Pen(TopLine, 1), new Point(0.35, c), new Point(c, 0.35));
        }
        if (BottomLine is not null)
        {
            dc.DrawLine(new Pen(BottomLine, 1), new Point(0, size.Height - 0.5), new Point(size.Width - c, size.Height - 0.5));
        }
        if (LeadingEdge is not null)
        {
            dc.DrawRectangle(LeadingEdge, null, new Rect(0, c, 2, Math.Max(0, size.Height - c)));
        }
    }
}
