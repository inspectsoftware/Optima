using System.Windows;
using System.Windows.Controls;

namespace Optima.App.Controls;

/// <summary>
/// One row of the SETTINGS page: the label (Header) and a one-line hint on the left, the control
/// (Content) on the right. A longer explanation goes in Info, behind a small glyph beside the label,
/// so the page reads as a list of labels rather than a wall of text. Stacked puts the control under
/// the label instead, for content too wide to sit beside it. The page's search reads Header, Hint and
/// Info, and the names of the row's card and section.
/// </summary>
public sealed class SettingRow : HeaderedContentControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(
        nameof(Info), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey WrappedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Wrapped), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public static readonly DependencyProperty WrappedProperty = WrappedPropertyKey.DependencyProperty;

    // The room the label keeps beside the control before the control goes under it, as in the design:
    // 280 of text and the 24 gap, plus the row's padding on both sides.
    private const double LabelRoom = 280 + 24 + 40;

    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public string? Info { get => (string?)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    /// <summary>
    /// True while the row is too narrow for the label beside the control (a small window), and the
    /// control sits under the label. Side by side, a wide control left the label a few pixels: its
    /// name was cut off and the hint broke in the middle of words.
    /// </summary>
    public bool Wrapped => (bool)GetValue(WrappedProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        if (!Stacked && Content is UIElement control && !double.IsInfinity(constraint.Width))
        {
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetValue(WrappedPropertyKey, control.DesiredSize.Width + LabelRoom > constraint.Width);
        }
        return base.MeasureOverride(constraint);
    }
}
