using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>A stroke icon on the 24 px grid (Themes/Icons.xaml holds the geometries, Lucide-style, ISC licensed, plus Optima's own glyphs).</summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(18.0));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(1.75));

    public Geometry? Symbol
    {
        get => (Geometry?)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
