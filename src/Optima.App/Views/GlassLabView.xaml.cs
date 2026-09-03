using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Optima.App.Views;

/// <summary>The glass renderer prototype (Phase 1 gate).</summary>
public partial class GlassLabView : UserControl
{
    private const int SeriesLength = 60;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<double> _series = [];
    private readonly Random _rng = new();
    private readonly List<AnimationClock> _drift = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = 200;
    private int _frames;
    private double _frameStamp;
    private bool _renderingHooked;
    private Window? _window;
    private bool _dragging;
    private Point _dragOrigin;
    private Point _panelOrigin;

    public GlassLabView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Advance();
        Rows.ItemsSource = new[]
        {
            new Row("GPU", "NVIDIA GeForce RTX 4060 Laptop"),
            new Row("CPU", "AMD Ryzen 7 6800H, 16 threads"),
            new Row("RAM", "32 GB, 11.2 GB free"),
            new Row("OS", "Windows 11 Home 24H2"),
            new Row("Google Play Games", "running"),
            new Row("Critical Ops", "installed"),
            new Row("Virtual display", "not installed"),
        };
    }

    private sealed record Row(string Label, string Value);

    private bool MotionAllowed => SystemParameters.ClientAreaAnimation || ForceMotionBox?.IsChecked == true;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < SeriesLength; i++)
        {
            Advance(false);
        }
        Redraw();
        _tick.Start();
        StartDrift();
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
        }
        ChartCanvas.SizeChanged += (_, _) => Redraw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        StopDrift();
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
    }

    private void StartDrift()
    {
        StopDrift();
        if (!IsLoaded || !MotionAllowed || DriftBox?.IsChecked != true)
        {
            return;
        }
        Drive(Blob1, 0, 90, 12, TranslateTransform.XProperty);
        Drive(Blob1, 0, 50, 9, TranslateTransform.YProperty);
        Drive(Blob2, 0, -110, 14, TranslateTransform.XProperty);
        Drive(Blob2, 0, -60, 11, TranslateTransform.YProperty);
        Drive(Blob3, -60, 60, 16, TranslateTransform.XProperty);
        Drive(Blob3, 30, -30, 13, TranslateTransform.YProperty);
    }

    private void Drive(Shape blob, double from, double to, double seconds, DependencyProperty property)
    {
        blob.CacheMode ??= new BitmapCache();
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        var clock = animation.CreateClock();
        ((TranslateTransform)blob.RenderTransform).ApplyAnimationClock(property, clock);
        _drift.Add(clock);
    }

    private void StopDrift()
    {
        foreach (var clock in _drift)
        {
            clock.Controller?.Stop();
        }
        _drift.Clear();
    }

    private void OnDriftChanged(object sender, RoutedEventArgs e) => StartDrift();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        foreach (var clock in _drift)
        {
            clock.Controller?.Resume();
        }
        _tick.Start();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        foreach (var clock in _drift)
        {
            clock.Controller?.Pause();
        }
        _tick.Stop();
    }

    private void Advance(bool redraw = true)
    {
        _last = Math.Clamp(_last + (_rng.NextDouble() - 0.5) * 18, 120, 260);
        _series.Add(_last);
        if (_series.Count > SeriesLength)
        {
            _series.RemoveAt(0);
        }
        if (redraw)
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        var w = ChartCanvas.ActualWidth;
        var h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _series.Count < 2)
        {
            return;
        }
        var points = new PointCollection(_series.Count);
        for (var i = 0; i < _series.Count; i++)
        {
            var x = i * w / (SeriesLength - 1);
            var y = h - (_series[i] - 110) / 160 * h;
            points.Add(new Point(x, y));
        }
        Spark.Points = points;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _frames++;
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _frameStamp >= 0.5)
        {
            var fps = _frames / (now - _frameStamp);
            var motion = SystemParameters.ClientAreaAnimation ? "on" : ForceMotionBox?.IsChecked == true ? "forced" : "off (Windows)";
            Readout.Text = $"fps {fps:0}  frame {1000 / Math.Max(fps, 0.001):0.0} ms  motion {motion}";
            _frames = 0;
            _frameStamp = now;
        }
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (MotionAllowed)
        {
            Panel.SetLight(e.GetPosition(Panel));
        }
    }

    private void OnPanelDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragOrigin = e.GetPosition(Overlay);
        _panelOrigin = new Point(Canvas.GetLeft(Panel), Canvas.GetTop(Panel));
        Panel.CaptureMouse();
    }

    private void OnPanelMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        var p = e.GetPosition(Overlay);
        Canvas.SetLeft(Panel, _panelOrigin.X + (p.X - _dragOrigin.X));
        Canvas.SetTop(Panel, _panelOrigin.Y + (p.Y - _dragOrigin.Y));
    }

    private void OnPanelUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Panel.ReleaseMouseCapture();
    }
}
