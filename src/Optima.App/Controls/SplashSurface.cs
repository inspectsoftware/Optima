using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Optima.Core.Theming;

namespace Optima.App.Controls;

/// <summary>
/// Everything the launch splash shows, drawn in one element: the card, the mark assembling from its
/// five parts, the name, and the centre square opening into the main window. It runs on the splash's
/// own thread, so it uses no application resources (those belong to the main thread) and builds its
/// brushes itself. <see cref="SplashTimeline"/> decides what each moment looks like.
/// </summary>
public sealed class SplashSurface : FrameworkElement
{
    public const double CardWidth = 460;
    public const double CardHeight = 340;

    // The mark, traced from the logo artwork (1254 units square), relative to the centre of its square.
    private const double LogoCenterX = 642.5;
    private const double LogoCenterY = 622;
    private const double SquareHalf = 155;
    private const double MarkSize = 0.15;
    private static readonly Point MarkCenterInCard = new(CardWidth / 2, 132);

    private static readonly (Point[] Shape, Vector From, Point EdgeA, Point EdgeB)[] Blades =
    [
        // Clockwise from the top; each flies in from the corner it points away from.
        (Logo((368, 208), (1072, 208), (898, 385), (898, 350), (358, 350), (178, 398)), new Vector(0.7071, -0.7071), Logo1(402, 350), Logo1(898, 350)),
        (Logo((1051, 333), (1051, 888), (879, 888), (879, 495)), new Vector(0.7071, 0.7071), Logo1(879, 495), Logo1(879, 888)),
        (Logo((425, 855), (425, 888), (1051, 888), (910, 1040), (345, 1040), (178, 1105)), new Vector(-0.7071, 0.7071), Logo1(425, 888), Logo1(879, 888)),
        (Logo((358, 350), (402, 350), (402, 790), (205, 985), (205, 495)), new Vector(-0.7071, -0.7071), Logo1(402, 350), Logo1(402, 790)),
    ];

    private static readonly Color Ground = Color.FromRgb(0x07, 0x08, 0x0A);
    private static readonly Color SilverLight = Color.FromRgb(0xF4, 0xF5, 0xF7);
    private static readonly Color SilverDark = Color.FromRgb(0xB4, 0xB8, 0xC2);
    private static readonly Typeface NameFace = new(
        new FontFamily("Space Grotesk, Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(
        new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Started by the first frame that is actually drawn, not by construction: a new window takes a
    // moment to reach the screen, and the intro must not spend that moment playing to nobody.
    private readonly Stopwatch _clock = new();
    private readonly bool _animate;
    private readonly string _version;
    private readonly Brush _silver;
    private Color _accent = Color.FromRgb(0xE8, 0xB4, 0x5A);
    private string _status = "starting";
    private double _statusFraction;
    private double _shownFraction;
    private double? _openStartedMs;
    private Rect _cardStart = new(0, 0, CardWidth, CardHeight);
    private Rect _target = new(0, 0, CardWidth, CardHeight);
    private TaskCompletionSource? _opened;

    // Text is laid out once and drawn every frame: the name and the version never change, the
    // status only when startup names a new stage. Rebuilt when the window lands on another DPI.
    private double _textDpi;
    private FormattedText[]? _letters;
    private FormattedText? _statusText;
    private FormattedText? _versionText;
    private string _statusTextFor = string.Empty;

    public SplashSurface(bool animate, string version)
    {
        _animate = animate;
        _version = version;
        var silver = new LinearGradientBrush(SilverLight, SilverDark, new Point(-470, -420), new Point(430, 480))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        silver.Freeze();
        _silver = silver;
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Draws one fixed moment instead of the running clock; for rendering stills of the beats.</summary>
    internal double? FrozenAtMs { get; set; }

    /// <summary>Milliseconds until the intro has played in full.</summary>
    public double IntroRemainingMs => _animate ? Math.Max(0, SplashTimeline.IntroMs - _clock.Elapsed.TotalMilliseconds) : 0;

    public void SetStatus(string text, double fraction)
    {
        _status = text;
        _statusFraction = Math.Clamp(fraction, 0, 1);
        InvalidateVisual();
    }

    public void SetAccent(Color accent)
    {
        _accent = accent;
        InvalidateVisual();
    }

    /// <summary>
    /// Starts the last beat. <paramref name="cardStart"/> is where the card sits now and
    /// <paramref name="target"/> is the main window, both in this element's coordinates. The task
    /// completes when nothing of the splash is left on screen.
    /// </summary>
    public Task OpenAsync(Rect cardStart, Rect target)
    {
        _cardStart = cardStart;
        _target = target;
        if (!_animate)
        {
            return Task.CompletedTask;
        }
        _opened = new TaskCompletionSource();
        _openStartedMs = _clock.Elapsed.TotalMilliseconds;
        InvalidateVisual();
        return _opened.Task;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_animate)
        {
            return;
        }
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }
        if (_openStartedMs is { } started && _clock.Elapsed.TotalMilliseconds - started >= SplashTimeline.OpenMs)
        {
            _opened?.TrySetResult();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var now = FrozenAtMs ?? (_animate ? _clock.Elapsed.TotalMilliseconds : SplashTimeline.IntroMs);
        var frame = SplashTimeline.At(now, _openStartedMs is { } started ? now - started : null);
        var open = frame.Open;
        var card = Lerp(_cardStart, _target, open);
        var markCenter = new Point(_cardStart.X + MarkCenterInCard.X, _cardStart.Y + MarkCenterInCard.Y);
        var unit = MarkSize * frame.MarkScale;

        dc.PushOpacity(frame.Fade);

        // The card, with the centre square cut out of it once it starts to open: what shows through
        // the hole is the real main window underneath.
        var squareAtRest = new Rect(
            markCenter.X - SquareHalf * unit, markCenter.Y - SquareHalf * unit, 2 * SquareHalf * unit, 2 * SquareHalf * unit);
        Geometry cardShape = new RectangleGeometry(card, 10 * (1 - open), 10 * (1 - open));
        if (open > 0)
        {
            cardShape = new CombinedGeometry(GeometryCombineMode.Exclude, cardShape, new RectangleGeometry(Lerp(squareAtRest, _target, open)));
        }
        dc.DrawGeometry(Solid(Ground, 1), new Pen(Solid(_accent, 0.2 * (1 - open)), 1), cardShape);

        // The mark.
        dc.PushTransform(new TranslateTransform(markCenter.X, markCenter.Y));
        dc.PushTransform(new ScaleTransform(unit, unit));
        dc.PushTransform(new RotateTransform(frame.MarkRotation));

        var markShape = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var i = 0; i < Blades.Length; i++)
        {
            var blade = Blades[i];
            var state = frame.Blades[i];
            if (state.Opacity <= 0.004)
            {
                continue;
            }
            var slide = new TranslateTransform(blade.From.X * state.Offset, blade.From.Y * state.Offset);
            var shape = Polygon(blade.Shape);
            shape.Transform = slide;
            markShape.Children.Add(shape);

            dc.PushOpacity(state.Opacity);
            dc.DrawGeometry(_silver, null, shape);
            if (state.Flash > 0.004)
            {
                dc.PushTransform(slide);
                dc.DrawLine(new Pen(Solid(_accent, state.Flash), 7), blade.EdgeA, blade.EdgeB);
                dc.Pop();
            }
            dc.Pop();
        }

        if (frame.SquareFill > 0.004 && frame.SquareOpacity > 0.004)
        {
            var half = SquareHalf * frame.SquareScale;
            var square = new RectangleGeometry(new Rect(-half, -half, 2 * half, 2 * half))
            {
                Transform = new RotateTransform(frame.SquareRotation),
            };
            markShape.Children.Add(square);
            dc.PushOpacity(frame.SquareOpacity * frame.SquareFill);
            dc.DrawGeometry(_silver, null, square);
            dc.Pop();
        }

        if (frame.SweepOpacity > 0.004)
        {
            // One band of light across the silver, clipped to the mark so it never touches the card.
            dc.PushClip(markShape);
            var along = -760 + 1520 * frame.Sweep;
            var band = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(200 * frame.SweepOpacity), 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                ],
                new Point(along - 130, along - 130), new Point(along + 130, along + 130))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            dc.DrawRectangle(band, null, new Rect(-700, -700, 1400, 1400));
            dc.Pop();
        }

        dc.Pop();
        dc.Pop();
        dc.Pop();

        DrawText(dc, frame);
        dc.Pop();
    }

    private void DrawText(DrawingContext dc, SplashFrame frame)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var left = _cardStart.X;
        var top = _cardStart.Y;

        // The name, one letter at a time, tracked wide.
        const string name = "OPTIMA";
        const double tracking = 9;
        if (_letters is null || _textDpi != dpi)
        {
            _textDpi = dpi;
            _letters = name.Select(ch => new FormattedText(
                ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NameFace, 24, Solid(SilverLight, 1), dpi)).ToArray();
            _versionText = new FormattedText(_version, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 10,
                Solid(Color.FromRgb(0x6E, 0x74, 0x7E), 1), dpi);
            _statusText = null;
        }
        var letters = _letters;
        var width = letters.Sum(l => l.WidthIncludingTrailingWhitespace) + tracking * (letters.Length - 1);
        var x = left + (CardWidth - width) / 2;
        for (var i = 0; i < letters.Length; i++)
        {
            if (frame.LetterOpacity[i] > 0.004)
            {
                dc.PushOpacity(frame.LetterOpacity[i]);
                dc.DrawText(letters[i], new Point(x, top + 222 + frame.LetterRise[i]));
                dc.Pop();
            }
            x += letters[i].WidthIncludingTrailingWhitespace + tracking;
        }

        if (frame.TextOpacity <= 0.004)
        {
            return;
        }
        dc.PushOpacity(frame.TextOpacity);

        // Status: a hairline that fills as startup really progresses, with the stage named under it.
        _shownFraction += (_statusFraction - _shownFraction) * (_animate ? 0.12 : 1);
        const double lineWidth = 220;
        var lineLeft = left + (CardWidth - lineWidth) / 2;
        dc.DrawRectangle(Solid(_accent, 0.18), null, new Rect(lineLeft, top + 272, lineWidth, 2));
        dc.DrawRectangle(Solid(_accent, 1), null, new Rect(lineLeft, top + 272, lineWidth * _shownFraction, 2));

        if (_statusText is null || _statusTextFor != _status)
        {
            _statusTextFor = _status;
            _statusText = new FormattedText(_status, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 11,
                Solid(Color.FromRgb(0x9A, 0xA0, 0xAA), 1), dpi);
        }
        var status = _statusText;
        dc.DrawText(status, new Point(left + (CardWidth - status.Width) / 2, top + 284));
        var version = _versionText!;
        dc.DrawText(version, new Point(left + (CardWidth - version.Width) / 2, top + 304));
        dc.Pop();
    }

    private static Point[] Logo(params (double X, double Y)[] points)
        => points.Select(p => Logo1(p.X, p.Y)).ToArray();

    private static Point Logo1(double x, double y) => new(x - LogoCenterX, y - LogoCenterY);

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: false);
        }
        return geometry;
    }

    private static Rect Lerp(Rect from, Rect to, double amount) => new(
        from.X + (to.X - from.X) * amount,
        from.Y + (to.Y - from.Y) * amount,
        Math.Max(0, from.Width + (to.Width - from.Width) * amount),
        Math.Max(0, from.Height + (to.Height - from.Height) * amount));

    private static SolidColorBrush Solid(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        return brush;
    }
}
