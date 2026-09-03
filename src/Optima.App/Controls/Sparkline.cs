using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>A vector sparkline: one accent line over a soft fill, scaled to the min and max of the series (or to Minimum/Maximum when set).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double? Minimum { get => (double?)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public Sparkline()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || Values is null)
        {
            return;
        }
        var values = new List<double>();
        foreach (var item in Values)
        {
            if (item is double d && double.IsFinite(d))
            {
                values.Add(d);
            }
            else if (item is float f)
            {
                values.Add(f);
            }
            else if (item is int i)
            {
                values.Add(i);
            }
        }
        if (values.Count < 2)
        {
            return;
        }

        var min = Minimum ?? values.Min();
        var max = Maximum ?? values.Max();
        if (max - min < 1e-6)
        {
            max = min + 1;
        }
        var pad = 2.0;
        var stepX = (w - 2 * pad) / (values.Count - 1);
        Point At(int index) => new(
            pad + index * stepX,
            pad + (h - 2 * pad) * (1 - (values[index] - min) / (max - min)));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(At(0), false, false);
            ac.BeginFigure(new Point(pad, h), true, true);
            ac.LineTo(At(0), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                lc.LineTo(At(i), true, true);
                ac.LineTo(At(i), false, false);
            }
            ac.LineTo(new Point(pad + (values.Count - 1) * stepX, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var accent = (Stroke as SolidColorBrush)?.Color ?? Colors.Goldenrod;
        var fill = new LinearGradientBrush(
            Color.FromArgb(0x4D, accent.R, accent.G, accent.B),
            Color.FromArgb(0x00, accent.R, accent.G, accent.B),
            new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.75) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        var last = At(values.Count - 1);
        dc.DrawEllipse(Stroke, null, last, 2.5, 2.5);
    }
}
