using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Health.Checks;
using Optima.Core.Launch;
using Optima.Core.Models;
using Optima.Tests.Launch;
using Xunit;

namespace Optima.Tests.Health;

public sealed class ChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-checks-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);
    private readonly FakePowerService _power = new();
    private readonly FakeDetector _detector = new();
    private DateTimeOffset _now = DateTimeOffset.Now;
    private bool _sessionActive;

    public ChecksTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    private async Task<DiagnosticResult> PowerPlanFor(string profileName)
    {
        var settings = new SettingsService(_paths, _store, NullLogger<SettingsService>.Instance);
        await settings.SaveSettingsAsync(new AppSettings { SelectedProfileName = profileName });
        var profiles = new ProfileService(_paths, _store, NullLogger<ProfileService>.Instance);
        return await new PowerPlanCheck(profiles, settings, _power).RunAsync();
    }

    private Task<DiagnosticResult> StaleRestore()
        => new StaleDisplayRestoreCheck(_paths, () => _sessionActive, () => _now).RunAsync();

    private void LeaveMarker(TimeSpan age)
    {
        File.WriteAllLines(_paths.VddRestoreMarkerFile,
            [Path.Combine(_paths.BackupsDirectory, "vdd_settings-1789554030.xml"), @"C:\VirtualDisplayDriver\vdd_settings.xml"]);
        File.SetLastWriteTimeUtc(_paths.VddRestoreMarkerFile, (_now - age).UtcDateTime);
    }

    [Fact]
    public async Task APcWithoutThePlanAProfileAsksForIsToldBeforeTheLaunch()
    {
        // Balanced is all the fake lists, as a Modern Standby PC without vendor plans would.
        var result = await PowerPlanFor("Competitive 1080p240");

        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal("POWER_PLAN_UNAVAILABLE", result.IssueCode);
        Assert.Contains("Competitive 1080p240", result.Reason);
        Assert.Contains("High performance", result.Reason);
        Assert.Contains("It lists: Balanced", result.Reason);
    }

    [Fact]
    public async Task APlanThePcOffersPassesAndClosesTheIssue()
    {
        _power.Listed.Add(new PowerScheme(PowerPlanPolicy.HighPerformance, "High performance"));

        var result = await PowerPlanFor("Competitive 1080p240");

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Equal("POWER_PLAN_UNAVAILABLE", result.IssueCode);
    }

    [Fact]
    public async Task AProfileThatLeavesThePlanAlonePasses()
    {
        var result = await PowerPlanFor("Default");

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Equal("POWER_PLAN_UNAVAILABLE", result.IssueCode);
    }

    [Fact]
    public async Task NoMarkerMeansNoRestoreIsWaiting()
    {
        var result = await StaleRestore();

        Assert.Equal(DiagnosticStatus.Pass, result.Status);
        Assert.Equal("VDD_RESTORE_PENDING", result.IssueCode);
    }

    [Fact]
    public async Task AMarkerThatIsWeeksOldIsARestoreThatNeverRan()
    {
        LeaveMarker(TimeSpan.FromDays(20));

        var result = await StaleRestore();

        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal("VDD_RESTORE_PENDING", result.IssueCode);
        Assert.Contains((_now - TimeSpan.FromDays(20)).ToLocalTime().ToString("yyyy-MM-dd"), result.Reason);
        // Which backup would overwrite which file: the decision the player has to make.
        Assert.Contains("vdd_settings-1789554030.xml", result.Details);
        Assert.Contains(@"would overwrite: C:\VirtualDisplayDriver\vdd_settings.xml", result.Details);
    }

    [Fact]
    public async Task AFreshMarkerBelongsToASessionThatIsRunningOrJustEnded()
    {
        LeaveMarker(TimeSpan.FromHours(2));

        Assert.Equal(DiagnosticStatus.Pass, (await StaleRestore()).Status);
    }

    [Fact]
    public async Task AnOldMarkerIsNotStaleWhileASessionIsStillRunning()
    {
        // A session left running over a weekend: its marker is old and exactly where it should be.
        LeaveMarker(TimeSpan.FromDays(3));
        _sessionActive = true;

        Assert.Equal(DiagnosticStatus.Pass, (await StaleRestore()).Status);
    }

    [Fact]
    public async Task TheGameChecksNameTheIssueTheyStandFor()
    {
        _detector.Platform = null;
        _detector.Game = null;

        var platform = await new GooglePlayGamesCheck(_detector).RunAsync();
        var game = await new CriticalOpsCheck(_detector).RunAsync();

        Assert.Equal((DiagnosticStatus.Fail, "GPG_NOT_FOUND"), (platform.Status, platform.IssueCode));
        Assert.Equal((DiagnosticStatus.Fail, "GAME_NOT_FOUND"), (game.Status, game.IssueCode));
    }

    [Fact]
    public async Task AFoundGamePassesUnderTheSameCodeSoItClosesTheIssue()
    {
        var game = await new CriticalOpsCheck(_detector).RunAsync();

        Assert.Equal((DiagnosticStatus.Pass, "GAME_NOT_FOUND"), (game.Status, game.IssueCode));
    }

    [Fact]
    public void OnlyChecksThatAreQuickAndQuietRunUnasked()
    {
        IDiagnosticCheck[] unasked =
        [
            new GooglePlayGamesCheck(_detector),
            new CriticalOpsCheck(_detector),
            new DiskSpaceCheck(),
            new StaleDisplayRestoreCheck(_paths, () => false),
        ];
        Assert.All(unasked, check => Assert.True(check.Scope.HasFlag(CheckScope.Startup), check.Name));

        // Half a second of CPU sampling behind the user's back is not quiet.
        Assert.Equal(CheckScope.OnDemand, ((IDiagnosticCheck)new OptimaOverheadCheck()).Scope);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
