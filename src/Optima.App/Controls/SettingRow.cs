using System.Windows;
using System.Windows.Controls;

namespace Optima.App.Controls;

/// <summary>
/// One row of the SETTINGS page: the label (Header) and a one-line hint on the left, the control
/// (Content) on the right. A longer explanation goes in Info, behind a small glyph beside the label,
/// so the page reads as a list of labels rather than a wall of text. Stacked puts the control under
/// the label instead, for content too wide to sit beside it. The page's search reads Header and Hint.
/// </summary>
public sealed class SettingRow : HeaderedContentControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(
        nameof(Info), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public string? Info { get => (string?)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }
}
