using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Optima.Watchdog;

/// <summary>
/// Short diagnostic trace (§12): listens to DXGI present events with NO process filter and
/// counts presents per process id. Answers "which process actually presents frames" when the
/// filtered capture stays silent, e.g. because the presenter is not the emulator process.
/// Read-only observation of events Windows already emits; nothing touches any process.
/// </summary>
public static class EtwPresentProbe
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly int[] PresentStartEventIds = [42, 55];

    /// <summary>Runs the probe for the given duration and returns present counts per pid.</summary>
    public static async Task<Dictionary<int, long>> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        var counts = new Dictionary<int, long>();
        var gate = new object();

        // Distinct session name so a crashed capture session is never clobbered by a probe.
        using var session = new TraceEventSession("Optima-PresentProbe")
        {
            StopOnDispose = true,
        };
        // No process filter on purpose (that is the question the probe answers), but only the
        // present events: everything else the DXGI provider emits would be dropped anyway.
        session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue,
            new TraceEventProviderOptions { EventIDsToEnable = [.. PresentStartEventIds] });

        session.Source.Dynamic.All += OnManifestEvent;
        session.Source.AllEvents += OnAnyEvent;

        var processing = new Thread(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (Exception)
            {
                // Session disposed, processing ends.
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

        lock (gate)
        {
            return new Dictionary<int, long>(counts);
        }

        void OnAnyEvent(TraceEvent ev)
        {
            if (ev.ProviderGuid != DxgiProvider || !PresentStartEventIds.Contains((int)ev.ID))
            {
                return;
            }
            lock (gate)
            {
                counts[ev.ProcessID] = counts.GetValueOrDefault(ev.ProcessID) + 1;
            }
        }

        static void OnManifestEvent(TraceEvent ev)
        {
            // Subscribing Dynamic.All keeps manifest parsing active; AllEvents does the work.
        }
    }
}
