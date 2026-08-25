using Optima.Core.Models;
using Optima.Driver.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Optima.Tests.Driver;

public class MockVirtualDisplayProviderTests
{
    private static MockVirtualDisplayProvider CreateProvider()
        => new(NullLogger<MockVirtualDisplayProvider>.Instance);

    [Fact]
    public async Task FullLifecycle_EnableSetModeDisable()
    {
        var provider = CreateProvider();
        await provider.InitializeAsync();
        await provider.EnableDisplayAsync();

        Assert.True(await provider.IsDisplayActiveAsync());

        await provider.SetModeAsync(new DisplayMode(1920, 1080, 240));
        Assert.Equal(new DisplayMode(1920, 1080, 240), await provider.GetCurrentModeAsync());

        await provider.DisableDisplayAsync();
        Assert.False(await provider.IsDisplayActiveAsync());
        Assert.Null(await provider.GetCurrentModeAsync());
    }

    [Fact]
    public async Task SetMode_BeforeEnable_Throws()
    {
        var provider = CreateProvider();
        await provider.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SetModeAsync(new DisplayMode(1920, 1080, 240)));
    }

    [Fact]
    public async Task SetMode_UnsupportedMode_ThrowsFriendlyError()
    {
        var provider = CreateProvider();
        await provider.InitializeAsync();
        await provider.EnableDisplayAsync();

        var ex = await Assert.ThrowsAsync<OptimaException>(
            () => provider.SetModeAsync(new DisplayMode(640, 480, 999)));
        Assert.Equal("DISPLAY_MODE_UNSUPPORTED", ex.Error.Code);
    }

    [Fact]
    public async Task Enable_WithoutInitialize_Throws()
    {
        var provider = CreateProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EnableDisplayAsync());
    }

    [Fact]
    public async Task RestoreOriginalState_RevertsToInitializeTimeState()
    {
        var provider = CreateProvider();
        await provider.InitializeAsync(); // captured state: disabled
        await provider.EnableDisplayAsync();
        await provider.SetModeAsync(new DisplayMode(2560, 1440, 165));

        await provider.RestoreOriginalStateAsync();

        Assert.False(await provider.IsDisplayActiveAsync());
    }

    [Fact]
    public async Task FailureInjection_ThrowsOnNamedOperation()
    {
        var provider = CreateProvider();
        provider.FailOperation = nameof(provider.EnableDisplayAsync);
        await provider.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EnableDisplayAsync());
    }

    [Fact]
    public async Task SetResolution_KeepsCurrentRefreshRate()
    {
        var provider = CreateProvider();
        await provider.InitializeAsync();
        await provider.EnableDisplayAsync();
        await provider.SetModeAsync(new DisplayMode(1920, 1080, 240));

        await provider.SetResolutionAsync(2560, 1440);

        Assert.Equal(new DisplayMode(2560, 1440, 240), await provider.GetCurrentModeAsync());
    }
}
