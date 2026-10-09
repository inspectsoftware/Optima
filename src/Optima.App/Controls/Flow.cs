using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Optima.App.Services;
using Optima.Core.Theming;

namespace Optima.App.Controls;

/// <summary>
/// How things move, said in a template. A trigger sets one of these where it used to set a colour
/// or a visibility, and the element gets there on the shared curve instead of jumping. Colours are
/// never animated (the theme's brushes are frozen): a state is a layer whose opacity changes.
/// All of it arrives at once when animations are off (<see cref="Motion.Enabled"/>).
/// </summary>
public static class Flow
{
    /// <summary>What comes before the first number in a text, the number, and what comes after.</summary>
    private static readonly System.Text.RegularExpressions.Regex Printed = new(@"^(\D*?)(-?\d+(?:[.,]\d+)?)(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>Fades a layer in and out. The layer starts with Opacity="0" in the template.</summary>
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Flow), new PropertyMetadata(false, (d, e) =>
        {
            if (d is UIElement layer)
            {
                To(layer, UIElement.OpacityProperty, (bool)e.NewValue ? 1 : 0, MotionSpec.FastMs);
            }
        }));

    /// <summary>A pressed control goes down by a pixel.</summary>
    public static readonly DependencyProperty SinkProperty = DependencyProperty.RegisterAttached(
        "Sink", typeof(bool), typeof(Flow), new PropertyMetadata(false, (d, e) =>
        {
            if (d is UIElement element)
            {
                if (element.RenderTransform is not TranslateTransform { IsFrozen: false } shift)
                {
                    element.RenderTransform = shift = new TranslateTransform();
                }
                To(shift, TranslateTransform.YProperty, (bool)e.NewValue ? 1 : 0, MotionSpec.FastMs);
            }
        }));

    /// <summary>Turns an element to this angle, in degrees, about its RenderTransformOrigin.</summary>
    public static readonly DependencyProperty TurnProperty = DependencyProperty.RegisterAttached(
        "Turn", typeof(double), typeof(Flow), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is UIElement element)
            {
                if (element.RenderTransform is not RotateTransform { IsFrozen: false } turn)
                {
                    element.RenderTransform = turn = new RotateTransform();
                }
                To(turn, RotateTransform.AngleProperty, (double)e.NewValue, MotionSpec.MoveMs);
            }
        }));

    /// <summary>
    /// Every time the element comes into view it fades up and rises this many pixels into place:
    /// a popup opening, a panel being revealed, a notice appearing.
    /// </summary>
    public static readonly DependencyProperty ArriveProperty = DependencyProperty.RegisterAttached(
        "Arrive", typeof(double), typeof(Flow), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is UIElement element && (double)e.OldValue <= 0 && (double)e.NewValue > 0)
            {
                element.IsVisibleChanged += (sender, visible) =>
                {
                    if ((bool)visible.NewValue)
                    {
                        Motion.Rise((UIElement)sender, 0, GetArrive((UIElement)sender));
                    }
                };
            }
        }));

    /// <summary>
    /// Draws a mark in: the stroke runs along its own outline from start to end. On an
    /// <see cref="Icon"/> or on any shape. Set it after whatever sets the mark's symbol.
    /// </summary>
    public static readonly DependencyProperty DrawProperty = DependencyProperty.RegisterAttached(
        "Draw", typeof(bool), typeof(Flow), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not FrameworkElement mark)
            {
                return;
            }
            var seconds = Motion.Seconds(MotionSpec.MoveMs);
            if (!(bool)e.NewValue || seconds <= 0)
            {
                return;
            }

            mark.ApplyTemplate();
            if (FirstShape(mark) is not { } shape)
            {
                return;
            }

            // Dashes are counted in stroke widths. One dash as long as the whole outline, slid in
            // from beyond its start, is the outline being drawn.
            var length = Length(shape.RenderedGeometry) / Math.Max(0.1, shape.StrokeThickness) + 1;
            shape.StrokeDashArray = [length, length];
            var draw = new DoubleAnimation(length, 0, TimeSpan.FromSeconds(seconds)) { EasingFunction = Motion.Ease, FillBehavior = FillBehavior.Stop };
            // The dashes are only for the drawing: left on, they would cut a different mark shown later.
            draw.Completed += (_, _) => shape.StrokeDashArray = null;
            shape.BeginAnimation(Shape.StrokeDashOffsetProperty, draw);
        }));

    /// <summary>
    /// In place of Value on a progress bar or a meter: the bar goes to each new value over
    /// <see cref="MotionSpec.DataMs"/> instead of jumping to it.
    /// </summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(Flow), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is System.Windows.Controls.Primitives.RangeBase range && double.IsFinite((double)e.NewValue))
            {
                To(range, System.Windows.Controls.Primitives.RangeBase.ValueProperty, (double)e.NewValue, MotionSpec.DataMs);
            }
        }));

    /// <summary>
    /// A notice going away: it fades and gives up its height, so that the ones stacked with it
    /// close the gap instead of dropping into it. Whoever owns it removes it once this has run.
    /// </summary>
    public static readonly DependencyProperty LeaveProperty = DependencyProperty.RegisterAttached(
        "Leave", typeof(bool), typeof(Flow), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement element && (bool)e.NewValue)
            {
                var height = new ScaleTransform();
                element.LayoutTransform = height;
                To(element, UIElement.OpacityProperty, 0, MotionSpec.MoveMs);
                To(height, ScaleTransform.ScaleYProperty, 0, MotionSpec.MoveMs);
            }
        }));

    /// <summary>A dialog opening: every time it comes into view it fades up and grows the last few percent to its size.</summary>
    public static readonly DependencyProperty OpenProperty = DependencyProperty.RegisterAttached(
        "Open", typeof(bool), typeof(Flow), new PropertyMetadata(false, (d, e) =>
        {
            if (d is UIElement element && (bool)e.NewValue)
            {
                element.IsVisibleChanged += (sender, visible) =>
                {
                    var length = Motion.Duration(MotionSpec.MoveMs);
                    if (!(bool)visible.NewValue || length == TimeSpan.Zero)
                    {
                        return;
                    }
                    var opened = (UIElement)sender;
                    var grow = new ScaleTransform();
                    opened.RenderTransformOrigin = new Point(0.5, 0.5);
                    opened.RenderTransform = grow;
                    DoubleAnimation From(double start) => new() { From = start, Duration = length, EasingFunction = Motion.Ease, FillBehavior = FillBehavior.Stop };
                    var last = From(0.96);
                    // Glass samples the backdrop at the place layout last gave it, and a transform is not layout.
                    last.Completed += (_, _) => GlassPanel.RefreshBackdrops();
                    opened.BeginAnimation(UIElement.OpacityProperty, From(0));
                    grow.BeginAnimation(ScaleTransform.ScaleXProperty, From(0.96));
                    grow.BeginAnimation(ScaleTransform.ScaleYProperty, last);
                };
            }
        }));

    /// <summary>
    /// In place of Text on a number that is printed beside a meter: when only the number in the
    /// text changes ("43%" to "57%"), it counts there with the meter. Any other change of text is
    /// shown at once.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Flow), new PropertyMetadata(null, (d, e) =>
        {
            if (d is not System.Windows.Controls.TextBlock block)
            {
                return;
            }
            var text = e.NewValue as string ?? string.Empty;
            var from = Printed.Match(block.Text ?? string.Empty);
            var to = Printed.Match(text);
            var length = Motion.Duration(MotionSpec.DataMs);
            if (length == TimeSpan.Zero || !from.Success || !to.Success
                || from.Groups[1].Value != to.Groups[1].Value || from.Groups[3].Value != to.Groups[3].Value)
            {
                block.BeginAnimation(CountProperty, null);
                block.Text = text;
                return;
            }
            block.SetValue(CountShapeProperty, to);
            block.BeginAnimation(CountProperty, new DoubleAnimation(Number(from), Number(to), length) { EasingFunction = Motion.Ease });
        }));

    private static readonly DependencyProperty CountShapeProperty = DependencyProperty.RegisterAttached(
        "CountShape", typeof(System.Text.RegularExpressions.Match), typeof(Flow));

    /// <summary>The number on its way, written back into the text it came from, with as many decimals.</summary>
    private static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(double), typeof(Flow), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is System.Windows.Controls.TextBlock block && d.GetValue(CountShapeProperty) is System.Text.RegularExpressions.Match shape)
            {
                var digits = shape.Groups[2].Value;
                var mark = digits.IndexOfAny(['.', ',']);
                var number = ((double)e.NewValue).ToString("F" + (mark < 0 ? 0 : digits.Length - mark - 1), System.Globalization.CultureInfo.InvariantCulture);
                block.Text = shape.Groups[1].Value + (mark < 0 ? number : number.Replace('.', digits[mark])) + shape.Groups[3].Value;
            }
        }));

    private static double Number(System.Text.RegularExpressions.Match printed)
        => double.Parse(printed.Groups[2].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);

    public static void SetLeave(DependencyObject element, bool value) => element.SetValue(LeaveProperty, value);
    public static bool GetLeave(DependencyObject element) => (bool)element.GetValue(LeaveProperty);
    public static void SetOpen(DependencyObject element, bool value) => element.SetValue(OpenProperty, value);
    public static bool GetOpen(DependencyObject element) => (bool)element.GetValue(OpenProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);
    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);
    public static void SetShow(DependencyObject element, bool value) => element.SetValue(ShowProperty, value);
    public static bool GetShow(DependencyObject element) => (bool)element.GetValue(ShowProperty);
    public static void SetSink(DependencyObject element, bool value) => element.SetValue(SinkProperty, value);
    public static bool GetSink(DependencyObject element) => (bool)element.GetValue(SinkProperty);
    public static void SetTurn(DependencyObject element, double value) => element.SetValue(TurnProperty, value);
    public static double GetTurn(DependencyObject element) => (double)element.GetValue(TurnProperty);
    public static void SetArrive(DependencyObject element, double value) => element.SetValue(ArriveProperty, value);
    public static double GetArrive(DependencyObject element) => (double)element.GetValue(ArriveProperty);
    public static void SetDraw(DependencyObject element, bool value) => element.SetValue(DrawProperty, value);
    public static bool GetDraw(DependencyObject element) => (bool)element.GetValue(DrawProperty);

    private static void To(IAnimatable target, DependencyProperty property, double value, int milliseconds)
        => target.BeginAnimation(property, new DoubleAnimation(value, Motion.Duration(milliseconds)) { EasingFunction = Motion.Ease });

    private static Shape? FirstShape(DependencyObject root)
    {
        if (root is Shape shape)
        {
            return shape;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FirstShape(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static double Length(Geometry outline)
    {
        var total = 0.0;
        foreach (var figure in outline.GetFlattenedPathGeometry().Figures)
        {
            var from = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                foreach (var point in segment is PolyLineSegment many ? many.Points : [((LineSegment)segment).Point])
                {
                    total += (point - from).Length;
                    from = point;
                }
            }
        }
        return total;
    }
}