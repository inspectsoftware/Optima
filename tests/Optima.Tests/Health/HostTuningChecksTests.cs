using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Health;
using Optima.Core.Health.Checks;
using Optima.Core.Models;
using Optima.Platform.Windows.Services;
using Xunit;

namespace Optima.Tests.Health;

public sealed class HostTuningChecksTests
{
    private readonly FakeProbe _probe = new();

    [Fact]
    public async Task MemoryIntegrityRunningIsANoteThatNamesWhereToChooseAndThatOptimaLeavesIt()
    {
        _probe.MemoryIntegrity = new MemoryIntegrityReading([MemoryIntegrityCheck.HvciService], 1);

        var result = await new MemoryIntegrityCheck(_probe).RunAsync();

        Assert.Equal((DiagnosticStatus.Warning, "MEMORY_INTEGRITY_ON"), (result.Status, result.IssueCode));
        Assert.Contains("frame rate", result.Reason);
        Assert.Contains("Core isolation", result.RecommendedFix);
        Assert.Contains("never changes it", result.RecommendedFix);
        Assert.Contains("SecurityServicesRunning: {2}", result.Details);
        Assert.Equal(IssueSeverity.Note, LogRuleTable.SeverityFor("MEMORY_INTEGRITY_ON", IssueSeverity.Warning));
    }

    [Fact]
    public async Task MemoryIntegritySwitchedOnButShownNotRunningPassesAndSaysItCostsNothingNow()
    {
        _probe.MemoryIntegrity = new MemoryIntegrityReading([], 1);

        var result = await new MemoryIntegrityCheck(_probe).RunAsync();

        Assert.Equal((DiagnosticStatus.Pass, "MEMORY_INTEGRITY_ON"), (result.Status, result.IssueCode));
        Assert.Contains("not running", result.Reason);
    }

    [Fact]
    public async Task MemoryIntegritySwitchedOnCountsAsOnWhenDeviceGuardCannotBeAsked()
    {
        _probe.MemoryIntegrity = new MemoryIntegrityReading(null, 1);

        var result = await new MemoryIntegrityCheck(_probe).RunAsync();

        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Contains("starts with Windows", result.Reason);
    }

    [Fact]
    public async Task MemoryIntegrityReadFromTheRegistryAloneWhenDeviceGuardCannotBeAsked()
    {
        _probe.MemoryIntegrity = new MemoryIntegrityReading(null, 0);

        var result = await new MemoryIntegrityCheck(_probe).RunAsync();

        Assert.Equal((DiagnosticStatus.Pass, "MEMORY_INTEGRITY_ON"), (result.Status, result.IssueCode));
        Assert.Contains("could not be read", result.Details);
    }

    [Fact]
    public async Task MemoryIntegrityIsSkippedWhenNeitherSourceAnswers()
    {
        _probe.MemoryIntegrity = new MemoryIntegrityReading(null, null);

        Assert.Equal(DiagnosticStatus.Skipped, (await new MemoryIntegrityCheck(_probe).RunAsync()).Status);
    }

    [Fact]
    public async Task TheRootSchedulerPassesAndClosesTheNote()
    {
        _probe.Scheduler = new HypervisorSchedulerReading(HypervisorSchedulerCheck.RootScheduler, DateTimeOffset.Now);

        var result = await new HypervisorSchedulerCheck(_probe).RunAsync();

        Assert.Equal((DiagnosticStatus.Pass, "HYPERVISOR_SCHEDULER_NOT_ROOT"), (result.Status, result.IssueCode));
        Assert.Contains("scheduler type 0x4", result.Details);
    }

    [Fact]
    public async Task AnotherSchedulerIsANoteThatCorePlacementMayNotReachTheGame()
    {
        _probe.Scheduler = new HypervisorSchedulerReading(3, DateTimeOffset.Now);

        var result = await new HypervisorSchedulerCheck(_probe).RunAsync();

        Assert.Equal((DiagnosticStatus.Warning, "HYPERVISOR_SCHEDULER_NOT_ROOT"), (result.Status, result.IssueCode));
        Assert.Contains("core scheduler", result.Reason);
        Assert.Contains("core placement may not reach the game", result.Reason);
        Assert.Equal(IssueSeverity.Note, LogRuleTable.SeverityFor("HYPERVISOR_SCHEDULER_NOT_ROOT", IssueSeverity.Warning));
    }

    [Fact]
    public async Task NoLoggedSchedulerIsSkipped()
    {
        Assert.Equal(DiagnosticStatus.Skipped, (await new HypervisorSchedulerCheck(_probe).RunAsync()).Status);
    }

    [Theory]
    [InlineData(1, "classic (SMT off)")]
    [InlineData(2, "classic")]
    [InlineData(3, "core")]
    [InlineData(4, "root")]
    [InlineData(9, "unknown (0x9)")]
    public void SchedulerTypesAreNamed(int type, string name)
        => Assert.Equal(name, HypervisorSchedulerCheck.Describe(type));

    [Fact]
    public async Task GpusOnMsiPassWithAnEvidenceLineEach()
    {
        _probe.Gpus.Add(new GpuInterruptReading("RTX", @"PCI\VEN_10DE", 1));
        _probe.Gpus.Add(new GpuInterruptReading("Radeon", @"PCI\VEN_1002", 1));

        var result = await new GpuInterruptModeCheck(_probe).RunAsync();

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Null(result.IssueCode);
        Assert.Contains("Every graphics card", result.Reason);
        Assert.Equal(2, result.Details.Split('\n').Length);
        Assert.Contains(@"RTX: MSI on (MSISupported = 1) (PCI\VEN_10DE)", result.Details);
    }

    [Fact]
    public async Task ALineBasedGpuIsInformationNeverAFixOrAnIssue()
    {
        _probe.Gpus.Add(new GpuInterruptReading("Old card", @"PCI\VEN_1234", null));
        _probe.Gpus.Add(new GpuInterruptReading("Turned off", @"PCI\VEN_5678", 0));

        var result = await new GpuInterruptModeCheck(_probe).RunAsync();

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Null(result.IssueCode);
        Assert.Empty(result.RecommendedFix);
        Assert.Contains("Old card, Turned off", result.Reason);
        Assert.Contains("line-based (MSISupported not set)", result.Details);
        Assert.Contains("line-based (MSISupported = 0)", result.Details);
    }

    [Fact]
    public async Task NoGpuIsSkipped()
    {
        Assert.Equal(DiagnosticStatus.Skipped, (await new GpuInterruptModeCheck(_probe).RunAsync()).Status);
    }

    [Fact]
    public void TheNotesRunUnaskedAndTheGpuReadWaitsToBeAsked()
    {
        Assert.Equal(CheckScope.Startup, ((IDiagnosticCheck)new MemoryIntegrityCheck(_probe)).Scope);
        Assert.Equal(CheckScope.Startup, ((IDiagnosticCheck)new HypervisorSchedulerCheck(_probe)).Scope);
        Assert.Equal(CheckScope.OnDemand, ((IDiagnosticCheck)new GpuInterruptModeCheck(_probe)).Scope);
    }

    [Fact]
    public async Task TheRealProbeReadsThisPcWithoutThrowing()
    {
        // Read-only: WMI, one event log query and registry keys opened for reading.
        var probe = new WindowsHostTuningProbe(NullLogger<WindowsHostTuningProbe>.Instance);

        await probe.ReadMemoryIntegrityAsync();
        await probe.ReadHypervisorSchedulerAsync();
        var gpus = await probe.ReadGpuInterruptsAsync();

        Assert.All(gpus, gpu => Assert.StartsWith(@"PCI\", gpu.InstanceId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeProbe : IHostTuningProbe
    {
        public MemoryIntegrityReading MemoryIntegrity { get; set; } = new(null, null);
        public HypervisorSchedulerReading? Scheduler { get; set; }
        public List<GpuInterruptReading> Gpus { get; } = [];

        public Task<MemoryIntegrityReading> ReadMemoryIntegrityAsync(CancellationToken ct = default) => Task.FromResult(MemoryIntegrity);

        public Task<HypervisorSchedulerReading?> ReadHypervisorSchedulerAsync(CancellationToken ct = default) => Task.FromResult(Scheduler);

        public Task<IReadOnlyList<GpuInterruptReading>> ReadGpuInterruptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GpuInterruptReading>>(Gpus);
    }
}
