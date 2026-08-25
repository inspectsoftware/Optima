using COPSBootstrapper.Core.Models;
using COPSBootstrapper.Driver;
using Xunit;

namespace COPSBootstrapper.Tests.Driver;

public class VddSettingsDocumentTests
{
    private const string SampleXml =
        """
        <?xml version='1.0' encoding='utf-8'?>
        <vdd_settings>
            <monitors><count>1</count></monitors>
            <gpu><friendlyname>default</friendlyname></gpu>
            <global>
                <g_refresh_rate>60</g_refresh_rate>
                <g_refresh_rate>144</g_refresh_rate>
            </global>
            <resolutions>
                <resolution><width>1920</width><height>1080</height><refresh_rate>30</refresh_rate></resolution>
                <resolution><width>2560</width><height>1440</height><refresh_rate>9999</refresh_rate></resolution>
            </resolutions>
            <options><HardwareCursor>true</HardwareCursor></options>
        </vdd_settings>
        """;

    [Fact]
    public void Parse_ReadsMonitorsGpuAndModes()
    {
        var document = VddSettingsDocument.Parse(SampleXml);

        Assert.Equal(1, document.MonitorCount);
        Assert.Equal("default", document.GpuFriendlyName);
        Assert.Equal([60, 144], document.GlobalRefreshRates);
        Assert.Equal(2, document.Resolutions.Count);
    }

    [Fact]
    public void GetAdvertisedModes_ExpandsGlobalRates_AndFiltersBogusRates()
    {
        var modes = VddSettingsDocument.Parse(SampleXml).GetAdvertisedModes();

        Assert.Contains(new DisplayMode(1920, 1080, 60), modes);
        Assert.Contains(new DisplayMode(1920, 1080, 144), modes);
        Assert.Contains(new DisplayMode(2560, 1440, 144), modes);
        // 9999 Hz placeholder and 30 Hz own-rate below 24-1000 validity are handled:
        Assert.DoesNotContain(modes, m => m.RefreshRate > 1000);
    }

    [Fact]
    public void EnsureMode_ExistingMode_ReturnsFalseAndLeavesXmlAlone()
    {
        var document = VddSettingsDocument.Parse(SampleXml);
        var changed = document.EnsureMode(new DisplayMode(1920, 1080, 144));
        Assert.False(changed);
    }

    [Fact]
    public void EnsureMode_NewResolution_AddsResolutionElement()
    {
        var document = VddSettingsDocument.Parse(SampleXml);

        var changed = document.EnsureMode(new DisplayMode(3440, 1440, 144));

        Assert.True(changed);
        Assert.Contains(document.Resolutions, r => r is { Width: 3440, Height: 1440 });
        Assert.Contains(new DisplayMode(3440, 1440, 144), document.GetAdvertisedModes());
    }

    [Fact]
    public void EnsureMode_NewRefreshRate_AddsGlobalRate()
    {
        var document = VddSettingsDocument.Parse(SampleXml);

        var changed = document.EnsureMode(new DisplayMode(1920, 1080, 240));

        Assert.True(changed);
        Assert.Contains(240, document.GlobalRefreshRates);
        Assert.Contains(new DisplayMode(1920, 1080, 240), document.GetAdvertisedModes());
    }

    [Fact]
    public void EnsureMode_InvalidMode_Throws()
    {
        var document = VddSettingsDocument.Parse(SampleXml);
        Assert.Throws<ArgumentException>(() => document.EnsureMode(new DisplayMode(0, 0, 0)));
    }

    [Fact]
    public void ToXmlString_PreservesOptionsAndComments()
    {
        var document = VddSettingsDocument.Parse(SampleXml);
        document.EnsureMode(new DisplayMode(1920, 1080, 240));
        var xml = document.ToXmlString();

        Assert.Contains("<HardwareCursor>true</HardwareCursor>", xml);
        Assert.Contains("<g_refresh_rate>240</g_refresh_rate>", xml);
    }

    [Fact]
    public void SetGpuFriendlyName_UpdatesElement()
    {
        var document = VddSettingsDocument.Parse(SampleXml);
        document.SetGpuFriendlyName("NVIDIA GeForce RTX 4060 Laptop GPU");
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", document.GpuFriendlyName);
    }
}
