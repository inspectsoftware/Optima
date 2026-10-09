using Optima.Core.Abstractions;
using Optima.Core.Launch;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Launch;

/// <summary>
/// "Check my setup" on the Display page: each step gets its own verdict and its own way out, a
/// failure stops the run where it happened, and whatever was changed is put back.
/// </summary>
public sealed class DisplaySetupCheckTests
{
    private sealed class FakeInstaller : IDriverInstaller
    {
        public DriverState State { get; set; } = DriverState.Installed;
        public DriverPackageInfo? FindBundledPackage() => null;
        public Task<DriverState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(State);
        public Task<DriverInstallResult> InstallAsync(CancellationToken ct = default) => Task.FromResult(DriverInstallResult.Ok());
        public Task<DriverInstallResult> UninstallAsync(CancellationToken ct = default) => Task.FromResult(DriverInstallResult.Ok());
    }

    private static readonly DisplayMode Mode = new(1920, 1080, 240);

    private readonly FakeInstaller _installer = new();
    private readonly FakeVirtualDisplay _display = new();
    private readonly FakeDisplayService _screens = new();
    private bool _helperPresent = true;
    private bool _restartPending;

    private Task<IReadOnlyList<DisplayCheckStep>> Run()
        => new DisplaySetupCheck(_installer, _display, _screens, () => _helperPresent, () => _restartPending)
            .RunAsync(Mode, hold: TimeSpan.Zero);

    private static DisplayCheckStep Step(IReadOnlyList<DisplayCheckStep> steps, string id) => steps.Single(s => s.Id == id);

    [Fact]
    public async Task AWorkingSetupPassesEveryStepAndIsPutBack()
    {
        var steps = await Run();

        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.Driver).State);
        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.Helper).State);
        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.Appears).State);
        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.Mode).State);
        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.PutBack).State);

        Assert.Contains("mode:1920x1080 @ 240 Hz", _display.Log);
        Assert.Equal("v1:topology", _screens.RestoredTopology);
        // Switched on by the check, so switched off by it.
        Assert.Contains("disable", _display.Log);
        Assert.False(_display.Active);
    }

    [Fact]
    public async Task ADisplayThatWasAlreadyOnIsLeftOn()
    {
        _display.Active = true;

        await Run();

        Assert.DoesNotContain("enable", _display.Log);
        Assert.DoesNotContain("disable", _display.Log);
    }

    [Fact]
    public async Task WithoutTheDriverNothingIsTouchedAndInstallIsOffered()
    {
        _installer.State = DriverState.NotInstalledPackageAvailable;

        var steps = await Run();

        var driver = Step(steps, DisplaySetupCheck.Driver);
        Assert.Equal(DisplayCheckState.Failed, driver.State);
        Assert.Equal(DisplayCheckFix.InstallDriver, driver.Fix);
        Assert.All(steps.Where(s => s.Id != DisplaySetupCheck.Driver), s => Assert.Equal(DisplayCheckState.NotReached, s.State));
        Assert.Empty(_display.Log);
        Assert.Empty(_screens.Log);
    }

    [Fact]
    public async Task ARestartThatIsStillOwedIsWhatTheDriverStepSays()
    {
        _restartPending = true;

        var driver = Step(await Run(), DisplaySetupCheck.Driver);

        Assert.Equal(DisplayCheckState.Failed, driver.State);
        Assert.Equal(DisplayCheckFix.RestartWindows, driver.Fix);
        Assert.Empty(_display.Log);
    }

    [Fact]
    public async Task AMissingHelperStopsTheRunBeforeTheDisplayIsTouched()
    {
        _helperPresent = false;

        var steps = await Run();

        Assert.Equal(DisplayCheckState.Failed, Step(steps, DisplaySetupCheck.Helper).State);
        Assert.Equal(DisplayCheckState.NotReached, Step(steps, DisplaySetupCheck.Appears).State);
        Assert.Empty(_display.Log);
    }

    [Fact]
    public async Task ADisplayThatDoesNotAppearOffersTheReload_AndTheScreensAreStillPutBack()
    {
        _display.EnableError = OptimaException.From(
            "VDD_NO_DISPLAY", "The virtual display did not appear.", "Windows never attached its display to the desktop.");

        var steps = await Run();

        var appears = Step(steps, DisplaySetupCheck.Appears);
        Assert.Equal(DisplayCheckState.Failed, appears.State);
        Assert.Equal(DisplayCheckFix.ReloadDriver, appears.Fix);
        Assert.Equal("VDD_NO_DISPLAY", appears.ErrorCode);
        Assert.Contains("did not appear", appears.Detail);
        Assert.Equal(DisplayCheckState.NotReached, Step(steps, DisplaySetupCheck.Mode).State);
        Assert.Equal(DisplayCheckState.Passed, Step(steps, DisplaySetupCheck.PutBack).State);
        Assert.Contains("restoreOriginal", _display.Log);
    }

    [Fact]
    public async Task ADeclinedPromptIsReportedWithNothingToRepair()
    {
        _display.EnableError = OptimaException.From(
            "ELEVATION_DECLINED", "Administrator access was declined.", "The prompt was answered with No.");

        var appears = Step(await Run(), DisplaySetupCheck.Appears);

        Assert.Equal(DisplayCheckState.Failed, appears.State);
        Assert.Equal(DisplayCheckFix.None, appears.Fix);
    }

    [Fact]
    public async Task ARestoreThatFailsIsAStepThatFailed_NotAnException()
    {
        _screens.RestoreTopologyErrors.Enqueue(new InvalidOperationException("no such display"));

        var restore = Step(await Run(), DisplaySetupCheck.PutBack);

        Assert.Equal(DisplayCheckState.Failed, restore.State);
        Assert.Contains("no such display", restore.Detail);
    }

    [Fact]
    public async Task EveryChangeOfAStepIsReportedAsItHappens()
    {
        var seen = new List<string>();

        await new DisplaySetupCheck(_installer, _display, _screens, () => true, () => false).RunAsync(
            Mode, hold: TimeSpan.Zero,
            onStep: steps => seen.Add(string.Join(",", steps.Select(s => s.State.ToString()[0]))));

        // First report: the driver step is running and nothing else has been reached.
        Assert.Equal("R,N,N,N,N", seen[0]);
        // Last report: all passed.
        Assert.Equal("P,P,P,P,P", seen[^1]);
    }
}
