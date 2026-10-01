using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Optima.App.Controls;

/// <summary>
/// Vector rank emblem for the Critical Ops ladder: a tier-colored shield with a per-tier motif and
/// the division chevrons (1-4). Drawn in-app because the game's own rank art is copyrighted and the
/// public API exposes no image urls — the tier color comes from CopsRankLadder's palette.
/// </summary>
public sealed class RankEmblem : FrameworkElement
{
    public static readonly DependencyProperty TierProperty = DependencyProperty.Register(
        nameof(Tier), typeof(int), typeof(RankEmblem), new FrameworkPropertyMetadata(-1, OnVisualChanged));

    public static readonly DependencyProperty DivisionProperty = DependencyProperty.Register(
        nameof(Division), typeof(int?), typeof(RankEmblem), new FrameworkPropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(RankEmblem), new FrameworkPropertyMetadata("#757D88", OnVisualChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(SizeValue), typeof(double), typeof(RankEmblem),
        new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Tier index → pack uri of the official media-kit icon ("elite" covers Elite Ops).
    /// The assembly short name is "Optima" (AssemblyName in the csproj), not "Optima.App".</summary>
    private static string?[] OfficialIconUris { get; } =
    [
        "pack://application:,,,/Optima;component/Assets/Ranks/unranked.png", // 0 calibrating
        "pack://application:,,,/Optima;component/Assets/Ranks/iron.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/bronze.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/silver.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/gold.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/platinum.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/diamond.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/master.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/specops.png",
        "pack://application:,,,/Optima;component/Assets/Ranks/elite.png",   // 9 elite ops
    ];

    /// <summary>Ladder tier index (0 = calibrating, 1 = iron … 9 = elite ops); -1 hides the motif.</summary>
    public int Tier
    {
        get => (int)GetValue(TierProperty);
        set => SetValue(TierProperty, value);
    }

    /// <summary>Division inside the tier (1-4) or null when the tier has none.</summary>
    public int? Division
    {
        get => (int?)GetValue(DivisionProperty);
        set => SetValue(DivisionProperty, value);
    }

    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    /// <summary>Rendered width; height follows the 64:72 shield aspect.</summary>
    public double SizeValue
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RankEmblem)d).InvalidateVisual();

    private const double DesignWidth = 64;
    private const double DesignHeight = 72;

    protected override Size MeasureOverride(Size availableSize)
        => new(SizeValue, SizeValue * DesignHeight / DesignWidth);

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 1 || height < 1)
        {
            return;
        }

        // Official Critical Ops art when a pack icon exists and loads; the vector shield is the
        // fallback (unknown tier or failed load) so the control always draws something.
        if (Tier >= 0 && Tier < OfficialIconUris.Length && OfficialIconUris[Tier] is { } uri
            && TryDrawOfficialIcon(context, uri, width, height))
        {
            return;
        }

        var tierColor = ParseColor(ColorHex);
        var bright = Shift(tierColor, 0.38);
        var dark = Shift(tierColor, -0.45);
        var motif = Shift(tierColor, 0.55);

        context.PushTransform(new ScaleTransform(width / DesignWidth, height / DesignHeight));

        // Shield: tier-colored gradient body with a dark rim.
        var shield = ShieldGeometry(6, 4, inset: 0);
        context.DrawGeometry(
            new LinearGradientBrush(bright, dark, 90),
            new Pen(new SolidColorBrush(dark), 3),
            shield);

        // Dark inner field so the motif reads on any tier color.
        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb(0xB4, 0x0E, 0x10, 0x13)),
            new Pen(Brushes.Black, 1),
            ShieldGeometry(11, 9, inset: 0));

        DrawMotif(context, motif);
        DrawDivisionChevrons(context, motif);

        context.Pop();
    }

    private static Geometry ShieldGeometry(double x, double y, double inset)
    {
        // Rounded-top shield with a pointed bottom, in the 64x72 design space.
        var left = x + inset;
        var right = DesignWidth - x - inset;
        var top = y + inset;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
            stream.LineTo(new Point(right, top), isStroked: true, isSmoothJoin: false);
            stream.LineTo(new Point(right, 34 - inset), isStroked: true, isSmoothJoin: false);
            stream.BezierTo(
                new Point(right, 50 - inset), new Point(right - 10, 57 - inset), new Point(DesignWidth / 2, 67 - inset * 1.6),
                isStroked: true, isSmoothJoin: true);
            stream.BezierTo(
                new Point(x + 10 + inset, 57 - inset), new Point(left, 50 - inset), new Point(left, 34 - inset),
                isStroked: true, isSmoothJoin: true);
            stream.LineTo(new Point(left, top), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawMotif(DrawingContext context, Color motif)
    {
        var pen = new Pen(new SolidColorBrush(motif), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fill = new SolidColorBrush(motif);
        switch (Tier)
        {
            case 0: // calibrating: three dots
                foreach (var x in new[] { 22.0, 32.0, 42.0 })
                {
                    context.DrawEllipse(fill, null, new Point(x, 31), 3.2, 3.2);
                }
                break;
            case 1: // iron: single ingot bar
                context.DrawLine(new Pen(fill, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(21, 31), new Point(43, 31));
                break;
            case 2: // bronze: two bars
                context.DrawLine(pen, new Point(21, 26), new Point(43, 26));
                context.DrawLine(pen, new Point(21, 37), new Point(43, 37));
                break;
            case 3: // silver: one big chevron
                DrawChevron(context, pen, 32, 38, 18, 9);
                break;
            case 4: // gold: chevron + bar
                DrawChevron(context, pen, 32, 35, 18, 9);
                context.DrawLine(pen, new Point(22, 43), new Point(42, 43));
                break;
            case 5: // platinum: diamond
                context.DrawGeometry(null, pen, Diamond(32, 31, 13));
                break;
            case 6: // diamond: faceted gem
                context.DrawGeometry(fill, pen, Diamond(32, 31, 13));
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x13)), 2), Diamond(32, 31, 6));
                break;
            case 7: // master: star
                context.DrawGeometry(fill, null, Star(32, 31, 14, 6));
                break;
            case 8: // spec ops: star with wings
                context.DrawGeometry(fill, null, Star(32, 30, 11, 4.6));
                context.DrawLine(pen, new Point(12, 44), new Point(24, 36));
                context.DrawLine(pen, new Point(52, 44), new Point(40, 36));
                break;
            case 9: // elite ops: three stars
                context.DrawGeometry(fill, null, Star(32, 25, 9, 3.8));
                context.DrawGeometry(fill, null, Star(17, 37, 6, 2.5));
                context.DrawGeometry(fill, null, Star(47, 37, 6, 2.5));
                break;
        }
    }

    private void DrawDivisionChevrons(DrawingContext context, Color color)
    {
        if (Division is not { } division || division <= 0)
        {
            return;
        }
        var pen = new Pen(new SolidColorBrush(color), 3.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var drawn = Math.Min(4, division);
        for (var i = 0; i < drawn; i++)
        {
            DrawChevron(context, pen, 32, 47 + i * 6.5, 13, 4);
        }
    }

    private static void DrawChevron(DrawingContext context, Pen pen, double centerX, double tipY, double halfWidth, double height)
    {
        context.DrawLine(pen, new Point(centerX - halfWidth, tipY - height), new Point(centerX, tipY));
        context.DrawLine(pen, new Point(centerX, tipY), new Point(centerX + halfWidth, tipY - height));
    }

    private static Geometry Diamond(double centerX, double centerY, double radius)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(centerX, centerY - radius), isFilled: true, isClosed: true);
            stream.LineTo(new Point(centerX + radius, centerY), isStroked: true, isSmoothJoin: false);
            stream.LineTo(new Point(centerX, centerY + radius), isStroked: true, isSmoothJoin: false);
            stream.LineTo(new Point(centerX - radius, centerY), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Star(double centerX, double centerY, double outerRadius, double innerRadius)
    {
        const int points = 5;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            Point At(double angle, double radius) => new(
                centerX + Math.Sin(angle) * radius,
                centerY - Math.Cos(angle) * radius);

            stream.BeginFigure(At(0, outerRadius), isFilled: true, isClosed: true);
            for (var i = 1; i < points * 2; i++)
            {
                var angle = i * Math.PI / points;
                stream.LineTo(At(angle, i % 2 == 0 ? outerRadius : innerRadius), isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static readonly Dictionary<string, BitmapCacheItem> BitmapCache = new(StringComparer.Ordinal);

    private sealed class BitmapCacheItem
    {
        public BitmapFrame? Frame { get; private set; }
        public bool Failed { get; private set; }

        public BitmapFrame? GetOrCreate(string uri)
        {
            if (Frame is not null || Failed)
            {
                return Frame;
            }

            try
            {
                Frame = BitmapFrame.Create(new Uri(uri, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Frame.Freeze();
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or System.IO.FileFormatException)
            {
                Failed = true;
            }
            return Frame;
        }
    }

    private static BitmapFrame? GetCachedFrame(string uri)
    {
        lock (BitmapCache)
        {
            if (!BitmapCache.TryGetValue(uri, out var cacheItem))
            {
                cacheItem = new BitmapCacheItem();
                BitmapCache[uri] = cacheItem;
            }
            return cacheItem.GetOrCreate(uri);
        }
    }

    /// <summary>Draws the official icon letterbox-fit; returns false when it could not be loaded
    /// so the caller falls back to the vector shield.</summary>
    private bool TryDrawOfficialIcon(DrawingContext context, string uri, double width, double height)
    {
        var frame = GetCachedFrame(uri);
        if (frame is null)
        {
            return false;
        }

        // The icons are square; letterbox-fit inside the control's box and center.
        var scale = Math.Min(width / frame.PixelWidth, height / frame.PixelHeight);
        var drawWidth = frame.PixelWidth * scale;
        var drawHeight = frame.PixelHeight * scale;
        var offset = new Point((width - drawWidth) / 2, (height - drawHeight) / 2);
        context.DrawImage(frame, new Rect(offset, new Size(drawWidth, drawHeight)));
        return true;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Color.FromRgb(0x75, 0x7D, 0x88);
        }
    }

    private static Color Shift(Color color, double amount)
    {
        byte F(byte value) => (byte)Math.Clamp(
            amount >= 0 ? value + (255 - value) * amount : value * (1 + amount), 0, 255);
        return Color.FromArgb(0xFF, F(color.R), F(color.G), F(color.B));
    }
}

