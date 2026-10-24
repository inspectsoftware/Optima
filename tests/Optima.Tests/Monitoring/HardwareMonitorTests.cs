using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Models;
using Optima.Monitoring;
using Xunit;

namespace Optima.Tests.Monitoring;

public sealed class HardwareMonitorTests
{
    [Fact]
    public async Task TheFirstSampleAfterEveryStart_MeasuresABusyGame()
    {
        // This test process stands in for the game, with one thread kept busy.
        using var stop = new CancellationTokenSource();
        var spin = Task.Run(() => { while (!stop.IsCancellationRequested) { } });
        await using var monitor = new HardwareMonitor(NullLogger<HardwareMonitor>.Instance);
        monitor.SetGameProcessIds([Environment.ProcessId]);
        try
        {
            // The monitor stops while the window is minimized or in the tray and starts on its return.
            for (var start = 0; start < 2; start++)
            {
                var first = new TaskCompletionSource<HardwareMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnSample(object? sender, HardwareMetrics metrics) => first.TrySetResult(metrics);
                monitor.MetricsUpdated += OnSample;
                await monitor.StartAsync();
                var metrics = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
                monitor.MetricsUpdated -= OnSample;
                await monitor.StopAsync();

                Assert.True(metrics.GameCpuPercent > 0, $"start {start + 1}: the game read {metrics.GameCpuPercent}%");
            }
        }
        finally
        {
            stop.Cancel();
            await spin;
        }
    }
}
