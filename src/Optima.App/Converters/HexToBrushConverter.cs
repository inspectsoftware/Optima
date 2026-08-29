using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Optima.Core.Theming;

namespace Optima.App.Converters;

/// <summary>Renders a hex color string (accent preset swatches) as a frozen brush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var argb = AccentMath.TryParse(value as string) ?? 0xFF808080;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF)));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
