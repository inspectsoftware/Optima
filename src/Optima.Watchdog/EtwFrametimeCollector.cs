using System.Collections.Concurrent;
using System.Globalization;
using Optima.Core.Ipc;
using Optima.Core.Statistics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Optima.Watchdog;

/// <summary>
/// External frametime capture (§12/§13): a real-time ETW session on the Microsoft-Windows-DXGI provider records
/// IDXGISwapChain::Present events for a set of candidate process ids, the PresentMon approach.
/// </summary>
public sealed class EtwFrametimeCollector : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    // The DXGI present events this capture counts. A set, not a list: the filter runs once per
    // event of the whole provider, tens of thousands of times a second in a game.
    private static readonly HashSet<int> PresentStartEventIds = [42, 55];

    private const int MaxFilteredPids = 8;

    /// <summary>
    /// Cap on events waiting to be aggregated. A dxgi present stream is a few hundred to a few
    /// thousand events per second, so this is only reachable if a producer misbehaves; the point
    /// is that a flood costs a bounded amount of memory instead of the capture growing without one.
    /// </summary>
    private const int MaxPendingEvents = 100_000;

    private readonly HashSet<int> _candidatePids;
    private readonly int _intervalMs;
    private readonly Func<IpcEvent, Task> _publish;
    private readonly object _lock = new();
    private readonly PresentWindowAggregator _aggregator;

    // Present events arrive on the ETW processing thread at the display's refresh rate, or
    // several times that in a game. The callback therefore only enqueues; the sample timer owns
    // the aggregator, so no frame of the game ever waits on a lock held by Optima.
    private readonly ConcurrentQueue<(int ProcessId, double TimestampMs)> _pending = new();
    private int _pendingCount;
    private int _droppedEvents;

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
            // The library's default is 64 MB, which Windows sets aside from non-paged memory for
            // as long as the session runs, beside the game. Two event ids, a few thousand events
            // a second at most and flushed every second, never come near 8.
            BufferSizeMB = 8,
        };
        var options = new TraceEventProviderOptions { EventIDsToEnable = [.. PresentStartEventIds] };
        if (_candidatePids.Count <= MaxFilteredPids)
        {
            options.ProcessIDFilter = [.. _candidatePids];
        }
        _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, options);
        HelperLog.Write("ETW present trace enabled, pid filter: "
            + (options.ProcessIDFilter is null ? "none (too many candidates)" : string.Join(',', options.ProcessIDFilter)));

        // AllEvents only: subscribing to Dynamic.All as well made the TraceEvent parser build a
        // dynamic payload object for every single present event, which is pure overhead here
        // because only the process id, the event id and the timestamp are used.
        _session.Source.AllEvents += OnAnyEvent;

        _processingThread = new Thread(() =>
        {
            try
            {
                _session.Source.Process();
            }
            catch (Exception)
            {
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
        if (ev.ProviderGuid != DxgiProvider || !_candidatePids.Contains(ev.ProcessID))
        {
            return;
        }
        if (!PresentStartEventIds.Contains((int)ev.ID))
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingCount) > MaxPendingEvents)
        {
            Interlocked.Decrement(ref _pendingCount);
            if (Interlocked.Increment(ref _droppedEvents) == 1)
            {
                HelperLog.Write($"More than {MaxPendingEvents} present events are waiting; dropping events until the next sample");
            }
            return;
        }
        _pending.Enqueue((ev.ProcessID, ev.TimeStampRelativeMSec));
    }

    private void DrainPendingEvents()
    {
        while (_pending.TryDequeue(out var present))
        {
            Interlocked.Decrement(ref _pendingCount);
            _aggregator.RecordPresent(present.ProcessId, present.TimestampMs);
        }
    }

    private void PublishSample(object? state)
    {
        PresentWindowSample? sample;
        lock (_lock)
        {
            DrainPendingEvents();
            sample = _aggregator.CompleteWindow();
        }
        if (sample is null)
        {
            return;
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
            // The tail of the capture still sits in the queue: the last sample window never got
            // to drain it because the session stopped first.
            DrainPendingEvents();
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
