using System.Windows;
using System.Windows.Media;
using Optima.Core.Configuration;
using Optima.Core.Models;
using Optima.Core.Theming;

namespace Optima.App.Services;

/// <summary>
/// Applies the theme palette (Dark/Light) and the user's accent family to
/// Application.Current.Resources at startup and live on every settings save.
/// All markup consumes these via DynamicResource, so a swap repaints in place.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly SettingsService _settings;
    private string _appliedTheme = "";
    private string _appliedAccent = "";

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Raised after a palette/accent apply; carries "Dark" or "Light". The shell
    /// listens to keep DWM window attributes (dark caption, backdrop fallback) in step.</summary>
    public static event Action<string>? ThemeApplied;

    /// <summary>The last applied theme name, for late subscribers.</summary>
    public static string CurrentTheme { get; private set; } = "Dark";

    /// <summary>The live accent, for code that draws with it (the ambient field).</summary>
    public static Color CurrentAccent { get; private set; } = Color.FromRgb(0xE8, 0xB4, 0x5A);

    /// <summary>Synchronous startup apply, before the main window shows (avoids a theme flash).</summary>
    public void Initialize(AppSettings settings) => Apply(settings);

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        if (dispatcher.CheckAccess())
        {
            Apply(settings);
        }
        else
        {
            dispatcher.Invoke(() => Apply(settings));
        }
    }

    private void Apply(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var accentHex = settings.AccentColor;
        if (theme == _appliedTheme && string.Equals(accentHex, _appliedAccent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (theme != _appliedTheme)
        {
            SwapPalette(app, theme);
            _appliedTheme = theme;
        }

        ApplyAccent(app, accentHex, theme);
        _appliedAccent = accentHex;

        CurrentTheme = theme;
        ThemeApplied?.Invoke(theme);
    }

    private static void SwapPalette(Application app, string theme)
    {
        var dictionaries = app.Resources.MergedDictionaries;
        var uri = new Uri($"Themes/Palette.{theme}.xaml", UriKind.Relative);
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null && source.Contains("Themes/Palette.", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = new ResourceDictionary { Source = uri };
                return;
            }
        }
        dictionaries.Insert(0, new ResourceDictionary { Source = uri });
    }

    private static void ApplyAccent(Application app, string? accentHex, string theme)
    {
        var accent = AccentMath.TryParse(accentHex) ?? AccentMath.TryParse(AccentMath.DefaultAccentHex)!.Value;

        // On paper the default gold is too bright; deepen any overly light accent so
        // hairlines and fills keep definition against the light ground.
        if (theme == "Light" && AccentMath.Luminance(accent) > 0.55)
        {
            accent = AccentMath.Darken(accent, 0.25);
        }

        var family = AccentMath.Derive(accent);
        SetColorAndBrush(app, "Color.Accent", "Brush.Accent", family.Base);
        SetColorAndBrush(app, "Color.OnAccent", "Brush.OnAccent", family.OnAccent);
        SetBrush(app, "Brush.AccentHover", family.Hover);
        SetBrush(app, "Brush.AccentPressed", family.Pressed);
        SetBrush(app, "Brush.AccentGlow", family.Glow);

        var baseColor = ToColor(family.Base);
        CurrentAccent = baseColor;

        // Accent-derived composites that are Freezables (gradients, drawings) and so cannot
        // follow the accent through DynamicResource on their own.
        var red = app.TryFindResource("Color.ChromaRed") is Color r ? r : Color.FromRgb(0xFF, 0x5A, 0x46);
        var blue = app.TryFindResource("Color.ChromaBlue") is Color b ? b : Color.FromRgb(0x5A, 0x96, 0xFF);
        var edge = new LinearGradientBrush(
        [
            new GradientStop(Color.FromArgb(0x99, red.R, red.G, red.B), 0),
            new GradientStop(baseColor, 0.4),
            new GradientStop(Color.FromArgb(0x99, blue.R, blue.G, blue.B), 1),
        ], new Point(0, 0), new Point(0, 1));
        edge.Freeze();
        app.Resources["Brush.Strip.Edge"] = edge;

        var active = new LinearGradientBrush(
        [
            new GradientStop(Color.FromArgb(0x29, baseColor.R, baseColor.G, baseColor.B), 0),
            new GradientStop(Color.FromArgb(0x00, baseColor.R, baseColor.G, baseColor.B), 1),
        ], new Point(0, 0), new Point(1, 0));
        active.Freeze();
        app.Resources["Brush.Nav.Active"] = active;

        var dim = Color.FromArgb(0x59, baseColor.R, baseColor.G, baseColor.B);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 6, 4))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(dim), null, new RectangleGeometry(new Rect(6, 0, 2, 4))));
        var track = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 4),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 4),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        track.Freeze();
        app.Resources["Brush.TrackFill"] = track;
    }

    private static void SetColorAndBrush(Application app, string colorKey, string brushKey, uint argb)
    {
        app.Resources[colorKey] = ToColor(argb);
        SetBrush(app, brushKey, argb);
    }

    private static void SetBrush(Application app, string key, uint argb)
    {
        var brush = new SolidColorBrush(ToColor(argb));
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    public void Dispose() => _settings.SettingsChanged -= OnSettingsChanged;
}
