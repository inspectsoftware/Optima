using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Optima.App.Controls;

/// <summary>A bounded meter drawn as block characters: ████████░░░░░░░░.</summary>
public sealed class AsciiBar : Control
{
    private const char Filled = '█';
    private const char Empty = '░';

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

    public static readonly DependencyProperty TextProperty = TextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey FilledTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(FilledText), typeof(string), typeof(AsciiBar), new PropertyMetadata(string.Empty));

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

/// <summary>Indeterminate counterpart to AsciiBar: a lit block travels along a fixed-width track.</summary>
/// <summary>Indeterminate counterpart to AsciiBar: a lit block sweeps along a fixed-width track.</summary>
public sealed class AsciiSpinner : Control
{
    private const double SweepStart = -18;
    private const double SweepEnd = 44;

    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(
        nameof(Cells), typeof(int), typeof(AsciiSpinner), new PropertyMetadata(14));

    private TranslateTransform? _sweep;

    public int Cells
    {
        get => (int)GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public AsciiSpinner()
    {
        IsVisibleChanged += (_, e) => SetRunning((bool)e.NewValue);
        Unloaded += (_, _) => SetRunning(false);
    }

    // The sweep is started from code rather than a template EventTrigger on Loaded: a
    // spinner that is collapsed when its page loads has no template tree yet, so a
    // storyboard targeting a template name throws and takes the whole app down.
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _sweep = (GetTemplateChild("SweepBar") as UIElement)?.RenderTransform as TranslateTransform;
        SetRunning(IsVisible);
    }

    private void SetRunning(bool running)
    {
        if (_sweep is null)
        {
            return;
        }
        if (running)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(SweepStart, SweepEnd, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
