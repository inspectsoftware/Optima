using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>A vector sparkline: one accent line over a soft fill, scaled to the min and max of the series (or to Minimum/Maximum when set).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

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

    // One buffer and two cached drawing resources per control: a live sparkline used to allocate a
    // list, two geometries, a gradient brush and a pen on every repaint, and it repaints on every
    // sample the host appends.
    private readonly List<double> _values = [];
    private Brush? _fill;
    private Color _fillAccent;
    private Pen? _pen;
    private Brush? _penStroke;

    public Sparkline()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    /// <summary>
    /// The series behind a sparkline is normally an observable collection that the host appends to
    /// every second. A dependency property only invalidates rendering when its *value* changes, and
    /// the collection instance never does, so the line would keep showing the first samples.
    /// </summary>
    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sparkline = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged previous)
        {
            previous.CollectionChanged -= sparkline.OnValuesCollectionChanged;
        }
        if (e.NewValue is INotifyCollectionChanged next)
        {
            next.CollectionChanged += sparkline.OnValuesCollectionChanged;
        }
    }

    private void OnValuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || Values is null)
        {
            return;
        }

        _values.Clear();
        foreach (var item in Values)
        {
            switch (item)
            {
                case double d when double.IsFinite(d):
                    _values.Add(d);
                    break;
                case float f:
                    _values.Add(f);
                    break;
                case int i:
                    _values.Add(i);
                    break;
            }
        }
        if (_values.Count < 2)
        {
            return;
        }

        var min = Minimum ?? double.MaxValue;
        var max = Maximum ?? double.MinValue;
        if (Minimum is null || Maximum is null)
        {
            foreach (var value in _values)
            {
                if (Minimum is null && value < min)
                {
                    min = value;
                }
                if (Maximum is null && value > max)
                {
                    max = value;
                }
            }
        }
        if (max - min < 1e-6)
        {
            max = min + 1;
        }

        var pad = 2.0;
        var stepX = (w - 2 * pad) / (_values.Count - 1);
        var span = max - min;

        Point At(int index) => new(
            pad + index * stepX,
            pad + (h - 2 * pad) * (1 - (_values[index] - min) / span));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(At(0), false, false);
            ac.BeginFigure(new Point(pad, h), true, true);
            ac.LineTo(At(0), false, false);
            for (var i = 1; i < _values.Count; i++)
            {
                lc.LineTo(At(i), true, true);
                ac.LineTo(At(i), false, false);
            }
            ac.LineTo(new Point(pad + (_values.Count - 1) * stepX, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var accent = (Stroke as SolidColorBrush)?.Color ?? Colors.Goldenrod;
        if (_fill is null || _fillAccent != accent)
        {
            var fill = new LinearGradientBrush(
                Color.FromArgb(0x4D, accent.R, accent.G, accent.B),
                Color.FromArgb(0x00, accent.R, accent.G, accent.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            _fill = fill;
            _fillAccent = accent;
        }
        // Deliberately not frozen: a theme brush may be unfrozen, and freezing a pen whose brush is
        // not frozen throws.
        if (_pen is null || !ReferenceEquals(_penStroke, Stroke))
        {
            _pen = new Pen(Stroke, 1.75) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _penStroke = Stroke;
        }

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _pen, line);
        dc.DrawEllipse(Stroke, null, At(_values.Count - 1), 2.5, 2.5);
    }
}
