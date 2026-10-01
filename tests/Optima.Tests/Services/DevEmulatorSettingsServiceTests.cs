using Optima.Platform.Windows.Services;
using Xunit;

namespace Optima.Tests.Services;

public class DevEmulatorSettingsServiceTests
{
    [Theory]
    [InlineData(null, GpuPreferenceKind.HighPerformance, "GpuPreference=2;")]
    [InlineData("", GpuPreferenceKind.HighPerformance, "GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;", GpuPreferenceKind.HighPerformance, "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;;", GpuPreferenceKind.PowerSaving, "SwapEffectUpgradeEnable=1;GpuPreference=1;")]
    [InlineData("GpuPreference=2;", GpuPreferenceKind.Auto, "")]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=2;", GpuPreferenceKind.Auto, "SwapEffectUpgradeEnable=1;")]
    public void ApplyGpuPreference_OnlyTouchesTheGpuComponent(string? current, GpuPreferenceKind kind, string expected)
    {
        Assert.Equal(expected, DevEmulatorSettingsService.ApplyGpuPreference(current, kind));
    }

    [Theory]
    [InlineData(null, GpuPreferenceKind.Auto)]
    [InlineData("", GpuPreferenceKind.Auto)]
    [InlineData("GpuPreference=2;", GpuPreferenceKind.HighPerformance)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", GpuPreferenceKind.PowerSaving)]
    [InlineData("SwapEffectUpgradeEnable=1;", GpuPreferenceKind.Auto)]
    [InlineData("GpuPreference=9;", GpuPreferenceKind.Auto)]
    public void ParseGpuPreference_RoundTripsTheRegistryString(string? raw, GpuPreferenceKind expected)
    {
        Assert.Equal(expected, DevEmulatorSettingsService.ParseGpuPreference(raw).Kind);
    }

    [Fact]
    public void MarkerFileName_MatchesGooglesNamingScheme()
    {
        Assert.Equal(
            "enable_idxgi_factory6_gpu_prioritization.v930690954.COMMITTED.01",
            DevEmulatorSettingsService.MarkerFileName("enable_idxgi_factory6_gpu_prioritization", 930690954, "COMMITTED"));
    }
}
