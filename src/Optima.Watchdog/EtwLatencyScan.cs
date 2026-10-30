using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Optima.Core.Health;

namespace Optima.Watchdog;

/// <summary>
/// The driver latency scan on DEBUG: a short kernel trace of every DPC and ISR, the way LatencyMon
/// measures them, added up per driver. Kernel events need administrator rights, which is why it
/// runs here. Only ever started by the player's click, so it is never in the way of a match.
/// </summary>
public static class EtwLatencyScan
{
    private const string SessionName = "Optima-LatencyScan";

    /// <summary>
    /// Stops a scan's trace left behind by a helper that was killed or crashed mid-scan. A kernel
    /// session outlives the process that made it, and this one logs every DPC and ISR on the machine
    /// into buffers nobody reads until the next scan or a reboot. Run when the helper starts, the
    /// first time it has the rights to stop it.
    /// </summary>
    public static void StopStale()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                TraceEventSession.GetActiveSession(SessionName)?.Stop();
                HelperLog.Write("stopped a latency scan trace left running by an earlier helper");
            }
        }
        catch (Exception ex)
        {
            HelperLog.Write("could not check for a leftover latency scan trace: " + ex.Message);
        }
    }

    public static async Task<IReadOnlyList<DriverLatency>> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        var tally = new DriverLatencyTally();

        // Its own name, so it neither takes over nor is taken over by the system's "NT Kernel
        // Logger" or another tool's trace: Windows 8 and later allow more than one kernel session.
        using var session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
            // A busy machine runs tens of thousands of DPCs a second; a few lost ones change nothing.
            BufferSizeMB = 16,
        };
        // The image events carry the drivers already loaded (the rundown at the start of the
        // session) as well as any loaded during it, which is what turns a routine's address into
        // the file of the driver that owns it.
        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.DeferedProcedureCalls
            | KernelTraceEventParser.Keywords.Interrupt
            | KernelTraceEventParser.Keywords.ImageLoad);

        var kernel = session.Source.Kernel;
        kernel.PerfInfoDPC += OnDpc;
        kernel.PerfInfoTimerDPC += OnDpc;
        // Threaded DPCs come under the same keyword with an event of their own, and some network and
        // storage stacks do all their deferred work in them.
        kernel.PerfInfoThreadedDPC += OnDpc;
        kernel.PerfInfoISR += e => tally.AddIsr(e.Routine, e.ElapsedTimeMSec * 1000);
        kernel.ImageDCStart += OnImage;
        kernel.ImageLoad += OnImage;
        kernel.ImageDCStop += OnImage;

        var processing = new Thread(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (Exception ex)
            {
                HelperLog.Write("Latency scan trace stopped: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "EtwLatencyScan",
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

        // Read only after the processing thread is done with it: the tally is not thread-safe.
        return tally.Rank();

        void OnDpc(DPCTraceData e) => tally.AddDpc(e.Routine, e.ElapsedTimeMSec * 1000);
        void OnImage(ImageLoadTraceData e) => tally.AddModule(e.ImageBase, e.ImageSize, e.FileName);
    }
}
