using System.Diagnostics;
using Optima.Platform.Windows.NativeMethods;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class ProcessSnapshotTests
{
    [Fact]
    public void ListsTheCurrentProcessUnderItsManagedName()
    {
        using var self = Process.GetCurrentProcess();

        var running = ProcessSnapshot.GetRunning();

        // Same pid and the same ".exe"-less name System.Diagnostics reports, so the detection
        // rules keep matching after the swap away from Process.GetProcesses().
        var entry = Assert.Single(running, p => p.Id == self.Id);
        Assert.Equal(self.ProcessName, entry.Name);
        Assert.True(running.Count > 10, "a Windows desktop has far more than ten processes");
    }
}
