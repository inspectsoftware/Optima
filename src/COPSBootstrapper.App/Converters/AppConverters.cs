using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using COPSBootstrapper.App.ViewModels;
using COPSBootstrapper.Core.Models;

namespace COPSBootstrapper.App.Converters;

/// <summary>StatusKind → dot/text brush.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusKind.Good => "Brush.Success",
            StatusKind.Warning => "Brush.Warning",
            StatusKind.Bad => "Brush.Error",
            _ => "Brush.TextMuted",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DiagnosticStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiagnosticStatus.Pass => "Brush.Success",
            DiagnosticStatus.Warning => "Brush.Warning",
            DiagnosticStatus.Fail => "Brush.Error",
            _ => "Brush.TextMuted",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DiagnosticStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            DiagnosticStatus.Pass => "PASS",
            DiagnosticStatus.Warning => "WARN",
            DiagnosticStatus.Fail => "FAIL",
            DiagnosticStatus.Skipped => "SKIP",
            _ => "?",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "ERROR" or "CRITICAL" => "Brush.Error",
            "WARN" => "Brush.Warning",
            "INFO" => "Brush.Info",
            _ => "Brush.TextMuted",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Null → Collapsed; non-null → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Collapsed (inverse of BooleanToVisibility).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Empty/whitespace string → Collapsed.</summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
