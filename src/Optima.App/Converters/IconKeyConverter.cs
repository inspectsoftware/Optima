using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Optima.App.Converters;

/// <summary>"Home" -> the Icon.Home geometry from Themes/Icons.xaml, for data-driven icons.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return null;
        }
        return Application.Current?.TryFindResource("Icon." + key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
