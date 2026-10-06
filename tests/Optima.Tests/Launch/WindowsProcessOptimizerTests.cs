using System.Diagnostics;
using Optima.Core.Models;
using Optima.Platform.Windows.NativeMethods;
using Optima.Platform.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Launch;

/// <summary>
/// Runs the real Windows optimizer against a throwaway child process, because the keeper's whole
/// promise is that it puts a setting back after something else changed it, and a fake cannot show
/// that the process APIs actually read and write what we think.
/// </summary>
public sealed class WindowsProcessOptimizerTests
{
    private static Process StartIdleChild()
    {
        // A process that just sits there for a minute. ping is used rather than timeout, which
        // refuses to run when it has no console to read from.
        var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(child);
        return child;
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    [Fact]
    public async Task Reassert_PutsBackAPriorityAndThrottlingSomethingElseChanged()
    {
        using var child = StartIdleChild();
        try
        {
            var optimizer = new WindowsProcessOptimizer(NullLogger<WindowsProcessOptimizer>.Instance);
            var profile = new PerformanceProfile
            {
                Priority = ProcessPriorityLevel.High,
                DisablePowerThrottling = true,
            };

            var baseline = await optimizer.ApplyAsync(child.Id, profile);
            Assert.NotNull(baseline);
            Assert.Equal(ProcessPriorityLevel.Normal, baseline.OriginalPriority);
            child.Refresh();
            Assert.Equal(ProcessPriorityClass.High, child.PriorityClass);

            // Something else on the machine demotes the game and re-enables EcoQoS.
            child.PriorityClass = ProcessPriorityClass.Normal;
            ProcessNative.SetPowerThrottling(child.Handle, enabled: true);
            Assert.True(ProcessNative.IsPowerThrottlingEnabled(child.Handle));

            await optimizer.ReassertAsync(baseline, profile);

            child.Refresh();
            Assert.Equal(ProcessPriorityClass.High, child.PriorityClass);
            Assert.False(ProcessNative.IsPowerThrottlingEnabled(child.Handle));

            // The keeper corrects the process; it must never rewrite the baseline it restores from.
            Assert.Equal(ProcessPriorityLevel.Normal, baseline.OriginalPriority);

            await optimizer.RestoreAsync(baseline);
            child.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, child.PriorityClass);
        }
        finally
        {
            Kill(child);
        }
    }

    [Fact]
    public async Task Reassert_PutsBackAnAffinityMaskSomethingElseChanged()
    {
        using var child = StartIdleChild();
        try
        {
            var optimizer = new WindowsProcessOptimizer(NullLogger<WindowsProcessOptimizer>.Instance);
            var profile = new PerformanceProfile { CpuAffinityMask = 1 };

            var baseline = await optimizer.ApplyAsync(child.Id, profile);
            Assert.NotNull(baseline);
            child.Refresh();
            Assert.Equal(1UL, (ulong)child.ProcessorAffinity.ToInt64());

            var everyone = (nint)((1L << Environment.ProcessorCount) - 1);
            child.ProcessorAffinity = everyone;
            Assert.Equal((ulong)everyone.ToInt64(), (ulong)child.ProcessorAffinity.ToInt64());

            await optimizer.ReassertAsync(baseline, profile);

            child.Refresh();
            Assert.Equal(1UL, (ulong)child.ProcessorAffinity.ToInt64());
        }
        finally
        {
            Kill(child);
        }
    }
}
