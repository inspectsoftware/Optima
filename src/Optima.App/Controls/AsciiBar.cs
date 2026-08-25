using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optima.App.Controls;

/// <summary>
/// A bounded meter drawn as block characters: <c>████████░░░░░░░░</c>.
/// Numbers are always shown beside one of these, never replaced by it: the bar gives
/// shape at a glance, the number is what actually gets read.
/// </summary>
public sealed class AsciiBar : Control
{
    private const char Filled = '█'; // █
    private const char Empty = '░';  // ░

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AsciiBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualInputChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(AsciiBar),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualInputChanged));

    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(
        nameof(Cells), typeof(int), typeof(AsciiBar),
        new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualInputChanged));

    private static readonly DependencyPropertyKey TextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Text), typeof(string), typeof(AsciiBar), new PropertyMetadata(string.Empty));

    /// <summary>The rendered block string; the control template binds to this.</summary>
    public static readonly DependencyProperty TextProperty = TextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey FilledTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(FilledText), typeof(string), typeof(AsciiBar), new PropertyMetadata(string.Empty));

    /// <summary>Just the filled run, so it can be tinted separately from the track.</summary>
    public static readonly DependencyProperty FilledTextProperty = FilledTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey EmptyTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(EmptyText), typeof(string), typeof(AsciiBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EmptyTextProperty = EmptyTextPropertyKey.DependencyProperty;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Number of block cells in the bar.</summary>
    public int Cells
    {
        get => (int)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public string Text => (string)GetValue(TextProperty);
    public string FilledText => (string)GetValue(FilledTextProperty);
    public string EmptyText => (string)GetValue(EmptyTextProperty);

    private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AsciiBar)d).Rebuild();

    public AsciiBar() => Rebuild();

    private void Rebuild()
    {
        var cells = Math.Clamp(Cells, 1, 200);
        var max = Maximum <= 0 ? 100 : Maximum;
        var ratio = double.IsFinite(Value) ? Math.Clamp(Value / max, 0, 1) : 0;
        var filled = (int)Math.Round(ratio * cells, MidpointRounding.AwayFromZero);

        var filledRun = new string(Filled, filled);
        var emptyRun = new string(Empty, cells - filled);

        SetValue(FilledTextPropertyKey, filledRun);
        SetValue(EmptyTextPropertyKey, emptyRun);
        SetValue(TextPropertyKey, filledRun + emptyRun);
    }
}

/// <summary>
/// Indeterminate counterpart to <see cref="AsciiBar"/>: a lit block travels along a
/// fixed-width track. Replaces the stock WPF indeterminate ProgressBar, whose animation
/// is baked into the Aero template and cannot be restyled.
/// </summary>
public sealed class AsciiSpinner : Control
{
    private const char Filled = '▓'; // ▓
    private const char Empty = '░';  // ░

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private int _position;
    private int _direction = 1;

    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(
        nameof(Cells), typeof(int), typeof(AsciiSpinner), new PropertyMetadata(14));

    public static readonly DependencyProperty HeadProperty = DependencyProperty.Register(
        nameof(Head), typeof(int), typeof(AsciiSpinner), new PropertyMetadata(3));

    private static readonly DependencyPropertyKey TextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Text), typeof(string), typeof(AsciiSpinner), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = TextPropertyKey.DependencyProperty;

    public int Cells
    {
        get => (int)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    /// <summary>Width of the travelling lit run.</summary>
    public int Head
    {
        get => (int)GetValue(HeadProperty);
        set => SetValue(HeadProperty, value);
    }

    public string Text => (string)GetValue(TextProperty);

    public AsciiSpinner()
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        _timer.Tick += (_, _) => Advance();

        // Only animate while actually on screen; an off-screen page must not keep a timer alive.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
        Unloaded += (_, _) => _timer.Stop();
        Render();
    }

    private void Advance()
    {
        var cells = Math.Clamp(Cells, 4, 200);
        var head = Math.Clamp(Head, 1, cells);
        _position += _direction;
        if (_position + head >= cells || _position <= 0)
        {
            _direction = -_direction;
            _position = Math.Clamp(_position, 0, cells - head);
        }
        Render();
    }

    private void Render()
    {
        var cells = Math.Clamp(Cells, 4, 200);
        var head = Math.Clamp(Head, 1, cells);
        var start = Math.Clamp(_position, 0, cells - head);

        var builder = new StringBuilder(cells);
        for (var i = 0; i < cells; i++)
        {
            builder.Append(i >= start && i < start + head ? Filled : Empty);
        }
        SetValue(TextPropertyKey, builder.ToString());
    }
}
