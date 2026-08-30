using Optima.Core.Updates;
using Xunit;

namespace Optima.Tests.Updates;

public sealed class LauncherUpdateParsingTests
{
    [Fact]
    public void ParsesTheRealReleaseJson()
    {
        var release = LauncherUpdateService.ParseLatestRelease(ReleaseFixtures.LatestReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release!.TagName);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.EndsWith(".zip", release.ZipName);
        Assert.Contains("Optima", release.ZipName);
        Assert.StartsWith("https://", release.ZipUrl);
        Assert.NotEqual(DateTimeOffset.MinValue, release.PublishedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"not-a-version","assets":[]}""")]
    [InlineData("""{"tag_name":"v9.9.9","assets":[{"name":"readme.txt","browser_download_url":"https://x/y"}]}""")]
    [InlineData("garbage")]
    public void UnusableShapesReturnNull(string json)
        => Assert.Null(LauncherUpdateService.ParseLatestRelease(json));

    [Fact]
    public void IsNewerComparesAgainstTheRunningVersion()
    {
        var current = LauncherUpdateService.CurrentVersion;

        var older = Make(new Version(0, 1, 0));
        var same = Make(new Version(current.Major, current.Minor, Math.Max(current.Build, 0)));
        var newer = Make(new Version(current.Major + 1, 0, 0));

        Assert.False(LauncherUpdateService.IsNewer(older));
        Assert.False(LauncherUpdateService.IsNewer(same));
        Assert.True(LauncherUpdateService.IsNewer(newer));

        static LauncherRelease Make(Version v) => new(
            "v" + v.ToString(3), v, "https://example/x.zip", "x.zip", "", DateTimeOffset.Now);
    }
}
