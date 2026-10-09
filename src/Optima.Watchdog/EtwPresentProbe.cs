using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Optima.Watchdog;

/// <summary>Short diagnostic trace (§12): listens to DXGI present events with NO process filter and counts presents per process id.</summary>
public static class EtwPresentProbe
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly HashSet<int> PresentStartEventIds = [42, 55];

    public static async Task<Dictionary<int, long>> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        var counts = new ConcurrentDictionary<int, long>();

        // Distinct session name so a crashed capture session is never clobbered by a probe.
        using var session = new TraceEventSession("Optima-PresentProbe")
        {
            StopOnDispose = true,
            // Not the library's 64 MB default; see EtwFrametimeCollector.Start.
            BufferSizeMB = 8,
        };
        // No process filter on purpose (that is the question the probe answers), but only the
        // present events: everything else the DXGI provider emits would be dropped anyway.
        session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue,
            new TraceEventProviderOptions { EventIDsToEnable = [.. PresentStartEventIds] });

        // AllEvents only: Dynamic.All would make the parser construct a payload object for every
        // present event the machine emits, and the probe only counts them by process id.
        session.Source.AllEvents += OnAnyEvent;

        var processing = new Thread(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (Exception)
            {
            }
        })
        {
            IsBackground = true,
            Name = "EtwPresentProbe",
        };
        processing.Start();

        try
        {
            await Task.Delay(duration, ct);
        }
        finally
        {
            session.Dispose();
            processing.Join(TimeSpan.FromSeconds(5));
        }

        return new Dictionary<int, long>(counts);

        void OnAnyEvent(TraceEvent ev)
        {
            if (ev.ProviderGuid != DxgiProvider || !PresentStartEventIds.Contains((int)ev.ID))
            {
                return;
            }
            counts.AddOrUpdate(ev.ProcessID, 1, static (_, current) => current + 1);
        }
    }
}
