using System.Globalization;
using COPSBootstrapper.Core.Ipc;
using COPSBootstrapper.Core.Statistics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace COPSBootstrapper.Elevated;

/// <summary>
/// External frametime capture (§12/§13): a real-time ETW session on the Microsoft-Windows-DXGI
/// provider records IDXGISwapChain::Present events for one process id — the PresentMon approach.
/// Nothing is injected into any process; this only listens to events Windows already emits.
/// Publishes one "etwSample" event per second (fps + average frametime) and returns aggregate
/// statistics on stop.
/// </summary>
public sealed class EtwFrametimeCollector : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    // Present_Start (42) and PresentMultiplaneOverlay_Start (55) mark a frame presentation.
    private static readonly int[] PresentStartEventIds = [42, 55];

    private readonly int _processId;
    private readonly Func<IpcEvent, Task> _publish;
    private readonly object _lock = new();
    private readonly List<double> _frametimesMs = [];
    private readonly List<double> _fpsSamples = [];

    private TraceEventSession? _session;
    private Thread? _processingThread;
    private Timer? _sampleTimer;
    private double _lastPresentMs = -1;
    private int _presentsInWindow;
    private double _frametimeSumInWindow;

    public EtwFrametimeCollector(int processId, Func<IpcEvent, Task> publish)
    {
        _processId = processId;
        _publish = publish;
    }

    public void Start()
    {
        // A stale session with the same name (e.g. after a crash) must be replaced.
        _session = new TraceEventSession("COPSBootstrapper-PresentTrace")
        {
            StopOnDispose = true,
        };
        _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);

        _session.Source.Dynamic.All += OnEvent;
        _session.Source.AllEvents += OnAnyEvent;

        _processingThread = new Thread(() =>
        {
            try
            {
                _session.Source.Process();
            }
            catch (Exception)
            {
                // Session disposed or lost — processing simply ends.
            }
        })
        {
            IsBackground = true,
            Name = "EtwPresentTrace",
        };
        _processingThread.Start();

        _sampleTimer = new Timer(PublishSample, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnAnyEvent(TraceEvent ev)
    {
        // Dynamic.All only sees manifest-resolved events; AllEvents is the safety net that
        // matches by provider + event id even when the manifest lookup fails.
        if (ev.ProviderGuid != DxgiProvider || ev.ProcessID != _processId)
        {
            return;
        }
        if (!PresentStartEventIds.Contains((int)ev.ID))
        {
            return;
        }
        RecordPresent(ev.TimeStampRelativeMSec);
    }

    private void OnEvent(TraceEvent ev)
    {
        // Handled by OnAnyEvent; subscribing Dynamic.All keeps manifest parsing active.
    }

    private void RecordPresent(double timestampMs)
    {
        lock (_lock)
        {
            if (_lastPresentMs >= 0)
            {
                var delta = timestampMs - _lastPresentMs;
                // Discard nonsense: paused game (>2 s) or duplicate/out-of-order timestamps.
                if (delta > 0.05 && delta < 2000)
                {
                    _frametimesMs.Add(delta);
                    _presentsInWindow++;
                    _frametimeSumInWindow += delta;
                }
            }
            _lastPresentMs = timestampMs;
        }
    }

    private void PublishSample(object? state)
    {
        double fps;
        double avgFrametime;
        lock (_lock)
        {
            if (_presentsInWindow == 0)
            {
                return; // game paused / minimized — publish nothing rather than zeros
            }
            fps = _presentsInWindow;
            avgFrametime = _frametimeSumInWindow / _presentsInWindow;
            _fpsSamples.Add(fps);
            _presentsInWindow = 0;
            _frametimeSumInWindow = 0;
        }

        _ = _publish(new IpcEvent
        {
            Kind = "etwSample",
            Data =
            {
                ["fps"] = fps.ToString("F1", CultureInfo.InvariantCulture),
                ["frametimeMs"] = avgFrametime.ToString("F3", CultureInfo.InvariantCulture),
            },
        });
    }

    /// <summary>Stops collection and returns aggregate statistics as IPC-friendly strings.</summary>
    public Dictionary<string, string> Stop()
    {
        _sampleTimer?.Dispose();
        _sampleTimer = null;
        _session?.Dispose();
        _session = null;
        _processingThread?.Join(TimeSpan.FromSeconds(5));
        _processingThread = null;

        List<double> frametimes;
        List<double> fpsSamples;
        lock (_lock)
        {
            frametimes = [.. _frametimesMs];
            fpsSamples = [.. _fpsSamples];
        }

        var stats = FrametimeStatistics.Compute(frametimes);
        return new Dictionary<string, string>
        {
            ["sampleCount"] = stats.SampleCount.ToString(CultureInfo.InvariantCulture),
            ["averageFps"] = stats.AverageFps.ToString("R", CultureInfo.InvariantCulture),
            ["onePercentLowFps"] = stats.OnePercentLowFps.ToString("R", CultureInfo.InvariantCulture),
            ["pointOnePercentLowFps"] = stats.PointOnePercentLowFps.ToString("R", CultureInfo.InvariantCulture),
            ["averageFrametimeMs"] = stats.AverageFrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["p95FrametimeMs"] = stats.P95FrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["p99FrametimeMs"] = stats.P99FrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["fpsSamples"] = string.Join(',', fpsSamples.Select(s => s.ToString("F1", CultureInfo.InvariantCulture))),
        };
    }

    public void Dispose()
    {
        _sampleTimer?.Dispose();
        _session?.Dispose();
        _processingThread?.Join(TimeSpan.FromSeconds(2));
    }
}
