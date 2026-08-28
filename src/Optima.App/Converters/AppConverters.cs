using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Optima.App.ViewModels;
using Optima.Core.Models;

namespace Optima.App.Converters;

/// <summary>
/// StatusKind → tint. Hue appears only inside status tags, so this is one of the few
/// places in the app that returns anything other than a gray.
/// </summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusKind.Good => "Brush.Ok",
            StatusKind.Warning => "Brush.Warn",
            StatusKind.Bad => "Brush.Fail",
            _ => "Brush.TextMuted",
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Bool negation for IsEnabled-style bindings ("editable while no plan is active").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class DiagnosticStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiagnosticStatus.Pass => "Brush.Ok",
            DiagnosticStatus.Warning => "Brush.Warn",
            DiagnosticStatus.Fail => "Brush.Fail",
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

/// <summary>
/// Bracket tag for a diagnostic row. Every result is padded to exactly six characters
/// so the tag column stays aligned down the whole checklist.
/// </summary>
public sealed class DiagnosticStatusToTagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            DiagnosticStatus.Pass => "[ OK ]",
            DiagnosticStatus.Warning => "[WARN]",
            DiagnosticStatus.Fail => "[FAIL]",
            DiagnosticStatus.Skipped => "[SKIP]",
            _ => "[ ?? ]",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True for Warning/Fail. Drives the indented fix line, which never shows under a pass.</summary>
public sealed class DiagnosticStatusIsProblemConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DiagnosticStatus.Warning or DiagnosticStatus.Fail
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "ERROR" or "CRITICAL" => "Brush.Fail",
            "WARN" => "Brush.Warn",
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

/// <summary>
/// Hides the DEVELOPER sidebar row unless developer mode is on; every other row is
/// always visible. Takes (navKey, developerModeVisible).
/// </summary>
public sealed class NavItemVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var key = values.Length > 0 ? values[0] as string : null;
        var developerVisible = values.Length > 1 && values[1] is true;
        return string.Equals(key, "DEVELOPER", StringComparison.OrdinalIgnoreCase) && !developerVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
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
