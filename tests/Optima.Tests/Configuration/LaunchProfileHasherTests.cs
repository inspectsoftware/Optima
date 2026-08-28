using Optima.Core.Configuration;
using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Configuration;

public sealed class LaunchProfileHasherTests
{
    private static LaunchProfile Profile(string name = "A") => new()
    {
        Name = name,
        Display = new DisplayProfile { VirtualDisplay = true, Width = 1920, Height = 1080, RefreshRate = 240 },
        Performance = new PerformanceProfile
        {
            PowerPlan = PowerPlanKind.HighPerformance,
            Priority = ProcessPriorityLevel.High,
            CleanupProcessNames = ["chrome", "discord"],
        },
    };

    [Fact]
    public void HashIsDeterministic()
        => Assert.Equal(LaunchProfileHasher.ComputeHash(Profile()), LaunchProfileHasher.ComputeHash(Profile()));

    [Fact]
    public void HashIs12LowercaseHexChars()
    {
        var hash = LaunchProfileHasher.ComputeHash(Profile());
        Assert.Equal(12, hash.Length);
        Assert.Matches("^[0-9a-f]{12}$", hash);
    }

    [Fact]
    public void RenameDoesNotChangeHash()
        => Assert.Equal(LaunchProfileHasher.ComputeHash(Profile("A")), LaunchProfileHasher.ComputeHash(Profile("Renamed")));

    [Fact]
    public void SettingEditChangesHash()
    {
        var edited = Profile() with { Display = new DisplayProfile { VirtualDisplay = true, Width = 1920, Height = 1080, RefreshRate = 165 } };
        Assert.NotEqual(LaunchProfileHasher.ComputeHash(Profile()), LaunchProfileHasher.ComputeHash(edited));
    }

    [Fact]
    public void CleanupListOrderDoesNotChangeHash()
    {
        var reordered = Profile() with
        {
            Performance = Profile().Performance with { CleanupProcessNames = ["discord", "chrome"] },
        };
        Assert.Equal(LaunchProfileHasher.ComputeHash(Profile()), LaunchProfileHasher.ComputeHash(reordered));
    }
}
