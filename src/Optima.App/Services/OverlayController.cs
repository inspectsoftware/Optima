using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Optima.App.ViewModels;
using Optima.App.Views;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Launch;
using Optima.Core.Models;
using Serilog;

namespace Optima.App.Services;

/// <summary>
/// Lifecycle of the in-game FPS overlay: shows it when a session reaches the Monitoring phase (PLAY and watch sessions
/// share that pipeline), hides it when the session ends, and Alt+F10 toggles it by hand.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private static readonly TimeSpan RepositionInterval = TimeSpan.FromSeconds(5);

    private readonly OverlayViewModel _viewModel;
    private readonly IGameWindowLocator _gameWindow;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _repositionTimer;

    private OverlayWindow? _window;
    private bool _overlayEnabled;
    private bool _showNetwork = true;
    private OverlayCorner _corner = OverlayCorner.TopRight;
    private double _opacity = 0.8;

    public OverlayController(
        OverlayViewModel viewModel,
        IGameWindowLocator gameWindow,
        SettingsService settings,
        LaunchOrchestrator orchestrator,
        INetworkQualityMonitor network)
    {
        _viewModel = viewModel;
        _gameWindow = gameWindow;
        _settings = settings;

        orchestrator.ProgressChanged += OnLaunchProgress;
        network.SampleArrived += OnNetworkSample;
        _settings.SettingsChanged += (_, s) => ApplySettings(s);
        _ = InitializeFromSettingsAsync();

        _repositionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RepositionInterval,
        };
        _repositionTimer.Tick += (_, _) => _ = RepositionAsync();
    }

    private async Task InitializeFromSettingsAsync()
    {
        try
        {
            ApplySettings(await _settings.GetSettingsAsync());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Overlay settings could not be read");
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _overlayEnabled = settings.OverlayEnabled;
        _showNetwork = settings.OverlayShowNetwork;
        _corner = OverlayPlacement.ParseCorner(settings.OverlayCorner);
        _opacity = Math.Clamp(settings.OverlayOpacity, 0.2, 1.0);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_window is not null)
            {
                _window.Opacity = _opacity;
            }
        });
    }

    private void OnLaunchProgress(object? sender, LaunchProgress progress)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            switch (progress.Phase)
            {
                case LaunchPhase.Monitoring when _overlayEnabled:
                    Show();
                    break;
                case LaunchPhase.Completed or LaunchPhase.Failed:
                    Hide();
                    break;
            }
        });
    }

    private void OnNetworkSample(object? sender, NetworkQualitySample sample)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var text = _showNetwork
                ? $"{sample.PingMs:F0} ms · {sample.JitterMs:F1} jit · {sample.PacketLossPct:F1}% loss"
                    + (sample.IsReferenceHost ? " [ REF ]" : string.Empty)
                : string.Empty;
            _viewModel.UpdateNetwork(text);
        });
    }

    public void Toggle()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_window is { IsVisible: true })
            {
                Hide();
            }
            else
            {
                Show();
            }
        });
    }

    private void Show()
    {
        // Ownerless on purpose: the overlay must sit over the game, not the app. The app's
        // ShutdownMode is OnMainWindowClose, so a hidden overlay cannot keep it alive.
        _window ??= new OverlayWindow { DataContext = _viewModel };
        _window.Opacity = _opacity;
        _window.Show();
        _repositionTimer.Start();
        _ = RepositionAsync();
    }

    private void Hide()
    {
        _repositionTimer.Stop();
        _window?.Hide();
    }

    private async Task RepositionAsync()
    {
        var window = _window;
        if (window is null || !window.IsVisible)
        {
            return;
        }

        try
        {
            var workAreaDevice = await _gameWindow.GetGameMonitorWorkAreaAsync();
            var dpi = VisualTreeHelper.GetDpi(window);

            var workArea = workAreaDevice is { } device
                ? new OverlayRect(
                    device.Left / dpi.DpiScaleX,
                    device.Top / dpi.DpiScaleY,
                    device.Width / dpi.DpiScaleX,
                    device.Height / dpi.DpiScaleY)
                : new OverlayRect(
                    SystemParameters.WorkArea.Left,
                    SystemParameters.WorkArea.Top,
                    SystemParameters.WorkArea.Width,
                    SystemParameters.WorkArea.Height);

            var (x, y) = OverlayPlacement.Compute(_corner, workArea, window.ActualWidth, window.ActualHeight);
            window.Left = x;
            window.Top = y;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Overlay reposition failed");
        }
    }

    public void Dispose()
    {
        _repositionTimer.Stop();
        _window?.Close();
        _window = null;
        _viewModel.Dispose();
    }
}
