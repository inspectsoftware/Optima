using Optima.Core.Configuration;
using Optima.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "optima-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonStore _store = new(NullLogger<JsonStore>.Instance);

    public ProfileServiceTests()
    {
        _paths = new AppPaths(_tempRoot);
        _paths.EnsureCreated();
    }

    private ProfileService CreateService() => new(_paths, _store, NullLogger<ProfileService>.Instance);

    [Fact]
    public async Task GetProfiles_AlwaysIncludesBuiltIns()
    {
        var profiles = await CreateService().GetProfilesAsync();

        Assert.Contains(profiles, p => p.Name == "Default");
        Assert.Contains(profiles, p => p.Name == "Balanced");
        Assert.Contains(profiles, p => p.Name.StartsWith("Competitive"));
        Assert.All(profiles, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public async Task SaveProfile_PersistsAcrossInstances()
    {
        var custom = new LaunchProfile
        {
            Name = "My 1440p",
            Display = new DisplayProfile { VirtualDisplay = true, Width = 2560, Height = 1440, RefreshRate = 165 },
            Performance = new PerformanceProfile { PowerPlan = PowerPlanKind.HighPerformance },
        };
        await CreateService().SaveProfileAsync(custom);

        var reloaded = await CreateService().GetProfilesAsync();
        var found = reloaded.FirstOrDefault(p => p.Name == "My 1440p");

        Assert.NotNull(found);
        Assert.Equal(165, found.Display.RefreshRate);
        Assert.False(found.IsBuiltIn);
    }

    [Fact]
    public async Task SaveProfile_RejectsBuiltInNames()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveProfileAsync(new LaunchProfile { Name = "Default" }));
    }

    [Fact]
    public async Task DeleteProfile_RemovesOnlyUserProfiles()
    {
        var service = CreateService();
        await service.SaveProfileAsync(new LaunchProfile { Name = "Deletable" });

        await service.DeleteProfileAsync("Deletable");
        await service.DeleteProfileAsync("Default"); // silently ignored

        var profiles = await service.GetProfilesAsync();
        Assert.DoesNotContain(profiles, p => p.Name == "Deletable");
        Assert.Contains(profiles, p => p.Name == "Default");
    }

    [Fact]
    public async Task ExportThenImport_RoundTrips()
    {
        var service = CreateService();
        await service.SaveProfileAsync(new LaunchProfile
        {
            Name = "Exported",
            Performance = new PerformanceProfile { DisablePowerThrottling = true },
        });
        var exportPath = Path.Combine(_tempRoot, "exported.json");

        await service.ExportProfileAsync("Exported", exportPath);
        await service.DeleteProfileAsync("Exported");
        var imported = await service.ImportProfileAsync(exportPath);

        Assert.Equal("Exported", imported.Name);
        Assert.True(imported.Performance.DisablePowerThrottling);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
