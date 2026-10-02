namespace Optima.Core.Configuration;

/// <summary>
/// The settings file the virtual display driver reads its modes from, and the content Optima
/// writes when it has to create one. Shared because two processes create it: the app (when it
/// installs the driver) and the elevated helper's one-shot install mode, which the setup runs.
/// </summary>
public static class VddSettingsDefaults
{
    /// <summary>The driver's own default location; a user can point the app somewhere else.</summary>
    public const string DefaultPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    /// <summary>One virtual monitor with the refresh rates the driver ships support for.</summary>
    public const string DefaultXml = """
        <?xml version='1.0' encoding='utf-8'?>
        <vdd_settings>
            <monitors>
                <count>1</count>
            </monitors>
            <gpu>
                <friendlyname>default</friendlyname>
            </gpu>
            <global>
                <g_refresh_rate>60</g_refresh_rate>
                <g_refresh_rate>90</g_refresh_rate>
                <g_refresh_rate>120</g_refresh_rate>
                <g_refresh_rate>144</g_refresh_rate>
                <g_refresh_rate>165</g_refresh_rate>
                <g_refresh_rate>240</g_refresh_rate>
            </global>
            <resolutions>
                <resolution><width>1280</width><height>720</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>2560</width><height>1440</height><refresh_rate>60</refresh_rate></resolution>
                <resolution><width>3840</width><height>2160</height><refresh_rate>60</refresh_rate></resolution>
            </resolutions>
            <options>
                <CustomEdid>false</CustomEdid>
                <PreventSpoof>false</PreventSpoof>
                <EdidCeaOverride>false</EdidCeaOverride>
                <HardwareCursor>true</HardwareCursor>
                <SDR10bit>false</SDR10bit>
                <HDRPlus>false</HDRPlus>
                <logging>false</logging>
                <debuglogging>false</debuglogging>
            </options>
        </vdd_settings>
        """;
}
