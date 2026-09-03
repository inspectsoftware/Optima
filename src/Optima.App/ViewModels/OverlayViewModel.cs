using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Optima.Core.Abstractions;

namespace Optima.App.ViewModels;

/// <summary>
/// Data for the in-game FPS overlay: the live sample stream with a staleness reset, because the ETW collector goes
/// silent (rather than reporting zeros) when the game stops presenting.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Staleness = TimeSpan.FromSeconds(5);

    private readonly IPerformanceMetricsProvider _metrics;
    private readonly DispatcherTimer _stalenessTimer;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;

    [ObservableProperty] private string _fpsText = "--";
    [ObservableProperty] private string _frametimeText = "-- ms";
    [ObservableProperty] private string _networkText = string.Empty;
    [ObservableProperty] private bool _networkVisible;

    public OverlayViewModel(IPerformanceMetricsProvider metrics)
    {
        _metrics = metrics;
        _metrics.SampleArrived += OnSample;
        _stalenessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _stalenessTimer.Tick += (_, _) =>
        {
            if (DateTimeOffset.Now - _lastSample > Staleness)
            {
                FpsText = "--";
                FrametimeText = "-- ms";
            }
        };
        _stalenessTimer.Start();
    }

    private void OnSample(object? sender, (double Fps, double FrametimeMs) sample)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _lastSample = DateTimeOffset.Now;
            FpsText = $"{sample.Fps:F0}";
            FrametimeText = $"{sample.FrametimeMs:F1} ms";
        });
    }

    public void UpdateNetwork(string text)
    {
        NetworkText = text;
        NetworkVisible = text.Length > 0;
    }

    public void Dispose()
    {
        _metrics.SampleArrived -= OnSample;
        _stalenessTimer.Stop();
    }
}
