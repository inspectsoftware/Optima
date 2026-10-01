using Optima.Core.Abstractions;
using Optima.Core.Models;
using Optima.Platform.Windows.Launchers;
using Optima.Tests.Launch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Launchers;

public sealed class DeveloperEmulatorLauncherTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-devemu-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDetector _detector = new();
    private bool _useDeveloperEmulator;

    public DeveloperEmulatorLauncherTests()
    {
        // A believable developer emulator layout: Bootstrapper.exe plus current\emulator\adb.exe.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "current", "emulator"));
        File.WriteAllText(Path.Combine(_tempRoot, "Bootstrapper.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_tempRoot, "current", "emulator", "adb.exe"), string.Empty);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private DeveloperEmulatorLauncher CreateLauncher()
    {
        _detector.Platform = new GooglePlayGamesInstallation { InstallDirectory = @"C:\Program Files\Google\Play Games" };
        return new DeveloperEmulatorLauncher(
            _detector,
            _ => Task.FromResult(new AppSettings { UseDeveloperEmulator = _useDeveloperEmulator }),
            _ => Task.FromResult(new DetectionRules { DeveloperEmulatorInstallPath = _tempRoot }),
            NullLogger<DeveloperEmulatorLauncher>.Instance);
    }

    private static InstalledGame Game => new()
    {
        PackageId = "com.criticalforceentertainment.criticalops",
        LaunchUri = "googleplaygames://launch/?id=com.criticalforceentertainment.criticalops",
    };

    [Fact]
    public void AdbArguments_HaveExpectedShape()
    {
        Assert.Equal("connect localhost:6520", DeveloperEmulatorLauncher.BuildConnectArgs());
        Assert.Equal(
            "-s localhost:6520 shell monkey -p com.criticalforceentertainment.criticalops 1",
            DeveloperEmulatorLauncher.BuildMonkeyArgs("com.criticalforceentertainment.criticalops"));
    }

    [Fact]
    public async Task CanLaunch_OffByDefault()
    {
        var launcher = CreateLauncher();

        Assert.False(await launcher.CanLaunchAsync(Game));
        Assert.False(launcher.IsEnabled);
    }

    [Fact]
    public async Task CanLaunch_TrueWhenOptedInAndAdbFound()
    {
        _useDeveloperEmulator = true;
        var launcher = CreateLauncher();

        Assert.True(await launcher.CanLaunchAsync(Game));
        Assert.True(launcher.IsEnabled);
    }

    [Fact]
    public async Task CanLaunch_FalseWhenAdbMissing()
    {
        _useDeveloperEmulator = true;
        File.Delete(Path.Combine(_tempRoot, "current", "emulator", "adb.exe"));
        var launcher = CreateLauncher();

        Assert.False(await launcher.CanLaunchAsync(Game));
    }

    [Fact]
    public async Task Launch_FailsGracefullyWithBrokenAdb()
    {
        // adb.exe exists but is an empty file, so process start fails; the launcher must
        // report failure instead of throwing.
        _useDeveloperEmulator = true;
        var launcher = CreateLauncher();

        Assert.False(await launcher.LaunchAsync(Game));
    }

    [Fact]
    public async Task ResolveInstallRoot_UsesRulesOverrideOverDetectedConsumerInstall()
    {
        // The detector reports the consumer install; the override must still win because the
        // developer emulator lives in its own folder.
        _useDeveloperEmulator = true;
        var launcher = CreateLauncher();

        Assert.True(await launcher.CanLaunchAsync(Game));
    }
}
