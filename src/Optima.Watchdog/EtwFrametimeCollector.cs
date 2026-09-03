using System.Globalization;
using Optima.Core.Ipc;
using Optima.Core.Statistics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Optima.Watchdog;

/// <summary>
/// External frametime capture (§12/§13): a real-time ETW session on the Microsoft-Windows-DXGI
/// provider records IDXGISwapChain::Present events for a set of candidate process ids, the
/// PresentMon approach. Nothing is injected into any process; this only listens to events
/// Windows already emits. The presenter is not always the emulator process itself, so each
/// window reports whichever candidate presented the most frames (see PresentWindowAggregator).
/// Publishes one "etwSample" event per interval and returns aggregate statistics on stop.
/// </summary>
public sealed class EtwFrametimeCollector : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    // Present_Start (42) and PresentMultiplaneOverlay_Start (55) mark a frame presentation.
    private static readonly int[] PresentStartEventIds = [42, 55];

    // MAX_EVENT_FILTER_PID_COUNT: ETW rejects a process id filter with more entries.
    private const int MaxFilteredPids = 8;

    private readonly HashSet<int> _candidatePids;
    private readonly int _intervalMs;
    private readonly Func<IpcEvent, Task> _publish;
    private readonly object _lock = new();
    private readonly PresentWindowAggregator _aggregator;

    private TraceEventSession? _session;
    private Thread? _processingThread;
    private Timer? _sampleTimer;

    public EtwFrametimeCollector(IReadOnlyCollection<int> candidatePids, int intervalMs, Func<IpcEvent, Task> publish)
    {
        _candidatePids = [.. candidatePids];
        _intervalMs = intervalMs;
        _publish = publish;
        _aggregator = new PresentWindowAggregator(candidatePids, intervalMs);
    }

    public void Start()
    {
        // A stale session with the same name (e.g. after a crash) must be replaced.
        _session = new TraceEventSession("Optima-PresentTrace")
        {
            StopOnDispose = true,
        };
        // Kernel-side filters: only the two present events, and (when the candidate list fits
        // ETW's eight-pid limit) only from the candidate processes. Without them every DXGI
        // event of every process on the machine lands in the session buffers, and the game
        // itself pays the per-event cost of emitting the ones the callback then discards.
        var options = new TraceEventProviderOptions { EventIDsToEnable = [.. PresentStartEventIds] };
        if (_candidatePids.Count <= MaxFilteredPids)
        {
            options.ProcessIDFilter = [.. _candidatePids];
        }
        _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, options);
        HelperLog.Write("ETW present trace enabled, pid filter: "
            + (options.ProcessIDFilter is null ? "none (too many candidates)" : string.Join(',', options.ProcessIDFilter)));

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
                // Session disposed or lost, so processing simply ends.
            }
        })
        {
            IsBackground = true,
            Name = "EtwPresentTrace",
        };
        _processingThread.Start();

        var interval = TimeSpan.FromMilliseconds(_intervalMs);
        _sampleTimer = new Timer(PublishSample, null, interval, interval);
    }

    private void OnAnyEvent(TraceEvent ev)
    {
        // Dynamic.All only sees manifest-resolved events; AllEvents is the safety net that
        // matches by provider + event id even when the manifest lookup fails.
        if (ev.ProviderGuid != DxgiProvider || !_candidatePids.Contains(ev.ProcessID))
        {
            return;
        }
        if (!PresentStartEventIds.Contains((int)ev.ID))
        {
            return;
        }
        lock (_lock)
        {
            _aggregator.RecordPresent(ev.ProcessID, ev.TimeStampRelativeMSec);
        }
    }

    private void OnEvent(TraceEvent ev)
    {
        // Handled by OnAnyEvent; subscribing Dynamic.All keeps manifest parsing active.
    }

    private void PublishSample(object? state)
    {
        PresentWindowSample? sample;
        lock (_lock)
        {
            sample = _aggregator.CompleteWindow();
        }
        if (sample is null)
        {
            return; // game paused / minimized, so publish nothing rather than zeros
        }

        _ = _publish(new IpcEvent
        {
            Kind = "etwSample",
            Data =
            {
                ["fps"] = sample.Fps.ToString("F1", CultureInfo.InvariantCulture),
                ["frametimeMs"] = sample.AverageFrametimeMs.ToString("F3", CultureInfo.InvariantCulture),
                ["pid"] = sample.ProcessId.ToString(CultureInfo.InvariantCulture),
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

        PresentCaptureResult result;
        lock (_lock)
        {
            result = _aggregator.Complete();
        }

        var stats = FrametimeStatistics.Compute(result.FrametimesMs);
        return new Dictionary<string, string>
        {
            ["sampleCount"] = stats.SampleCount.ToString(CultureInfo.InvariantCulture),
            ["averageFps"] = stats.AverageFps.ToString("R", CultureInfo.InvariantCulture),
            ["onePercentLowFps"] = stats.OnePercentLowFps.ToString("R", CultureInfo.InvariantCulture),
            ["pointOnePercentLowFps"] = stats.PointOnePercentLowFps.ToString("R", CultureInfo.InvariantCulture),
            ["averageFrametimeMs"] = stats.AverageFrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["p95FrametimeMs"] = stats.P95FrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["p99FrametimeMs"] = stats.P99FrametimeMs.ToString("R", CultureInfo.InvariantCulture),
            ["fpsSamples"] = string.Join(',', result.FpsSamples.Select(s => s.ToString("F1", CultureInfo.InvariantCulture))),
            ["dominantPid"] = result.DominantProcessId.ToString(CultureInfo.InvariantCulture),
        };
    }

    public void Dispose()
    {
        _sampleTimer?.Dispose();
        _session?.Dispose();
        _processingThread?.Join(TimeSpan.FromSeconds(2));
    }
}
