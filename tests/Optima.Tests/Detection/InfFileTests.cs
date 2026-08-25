using System.Runtime.InteropServices;
using Optima.Core.Detection;
using Xunit;

namespace Optima.Tests.Detection;

public class InfFileTests
{
    // Shape of a real IddCx virtual display INF: tokenized strings, a decorated Models
    // section, and the hardware id as the second value of the model line.
    private const string VirtualDisplayInf = """
        [Version]
        Signature="$WINDOWS NT$"
        Class=Display
        ClassGuid={4d36e968-e325-11ce-bfc1-08002be10318}
        Provider=%ManufacturerName%
        CatalogFile=MttVDD.cat
        DriverVer=01/01/2025,1.0.0.0

        [Manufacturer]
        %ManufacturerName%=Standard,NTamd64.10.0...16299

        [Standard.NTamd64.10.0...16299]
        %DeviceName% = MyDevice_Install, Root\MttVDD

        [Strings]
        ManufacturerName = "MikeTheTech"
        DeviceName = "Virtual Display Driver"
        """;

    [Fact]
    public void Parse_ReadsHardwareIdFromDecoratedModelsSection()
    {
        var info = InfFile.Parse(VirtualDisplayInf);
        Assert.Equal(@"Root\MttVDD", info.HardwareId);
    }

    [Fact]
    public void Parse_ResolvesStringTokens()
    {
        var info = InfFile.Parse(VirtualDisplayInf);
        Assert.Equal("MikeTheTech", info.Provider);
        Assert.Equal("Virtual Display Driver", info.Description);
    }

    [Fact]
    public void Parse_UndecoratedModelsSection()
    {
        const string inf = """
            [Version]
            Provider=%Mfg%

            [Manufacturer]
            %Mfg%=Models

            [Models]
            %Desc% = Install, Root\SimpleDevice

            [Strings]
            Mfg = "Acme"
            Desc = "Simple Device"
            """;

        var info = InfFile.Parse(inf);
        Assert.Equal(@"Root\SimpleDevice", info.HardwareId);
        Assert.Equal("Acme", info.Provider);
    }

    [Fact]
    public void Parse_PrefersDecoratedSectionOverPlainOne()
    {
        const string inf = """
            [Manufacturer]
            %Mfg%=Models,NTamd64

            [Models]
            %Desc% = Install, Root\WrongDevice

            [Models.NTamd64]
            %Desc% = Install, Root\RightDevice

            [Strings]
            Mfg = "Acme"
            Desc = "Device"
            """;

        Assert.Equal(@"Root\RightDevice", InfFile.Parse(inf).HardwareId);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        const string inf = """
            ; a leading comment
            [Manufacturer]

            ; another comment
            %Mfg%=Models    ; trailing comment

            [Models]
            %Desc% = Install, Root\Device

            [Strings]
            Mfg = "Acme"
            Desc = "Device"
            """;

        Assert.Equal(@"Root\Device", InfFile.Parse(inf).HardwareId);
    }

    [Fact]
    public void Parse_MissingModelsSection_ReturnsNullHardwareId()
    {
        const string inf = """
            [Version]
            Provider=%Mfg%

            [Strings]
            Mfg = "Acme"
            """;

        var info = InfFile.Parse(inf);
        Assert.Null(info.HardwareId);
        Assert.Equal("Acme", info.Provider);
    }

    [Fact]
    public void Parse_UnresolvableToken_FallsBackToLiteral()
    {
        const string inf = """
            [Manufacturer]
            %Mfg%=Models

            [Models]
            %Missing% = Install, Root\Device
            """;

        Assert.Equal("%Missing%", InfFile.Parse(inf).Description);
    }

    [Fact]
    public void Parse_EmptyInput_DoesNotThrow()
    {
        var info = InfFile.Parse(string.Empty);
        Assert.Null(info.HardwareId);
    }

    // ---- shapes taken from the real bundled packages ----

    // The shipped MttVDD.inf lists two hardware ids for the same device: a root-enumerated
    // one and a bus-style alternate used by the vendor's sample app. Only the root id can
    // be created as a device node, so it must win regardless of file order.
    private const string MttVddInf = """
        [Version]
        PnpLockdown=1
        Signature="$Windows NT$"
        ClassGUID = {4D36E968-E325-11CE-BFC1-08002BE10318}
        Class = Display
        Provider=%ManufacturerName%
        CatalogFile=MttVDD.cat
        DriverVer = 12/24/2024,11.30.4.434

        [Manufacturer]
        %ManufacturerName%=Standard,NTamd64

        [Standard.NTamd64]
        %DeviceName%=MyDevice_Install, Root\MttVDD  ; TODO: edit hw-id
        %DeviceName%=MyDevice_Install, MttVDD       ; used by IddSampleApp.exe

        [Strings]
        ManufacturerName="MikeTheTech"
        DeviceName="Virtual Display Driver"
        """;

    [Fact]
    public void Parse_RealVddInf_PrefersRootEnumeratedHardwareId()
    {
        var info = InfFile.Parse(MttVddInf);
        Assert.Equal(@"Root\MttVDD", info.HardwareId);
        Assert.Equal("Display", info.DeviceClass);
        Assert.Equal("MikeTheTech", info.Provider);
        Assert.Equal("Virtual Display Driver", info.Description);
    }

    [Fact]
    public void Parse_RealVddInf_ReportsX64Only()
    {
        var info = InfFile.Parse(MttVddInf);
        Assert.True(info.TargetsArchitecture(Architecture.X64));
        Assert.False(info.TargetsArchitecture(Architecture.Arm64));
    }

    [Fact]
    public void Parse_Arm64Variant_ReportsArm64Only()
    {
        var info = InfFile.Parse(MttVddInf.Replace("NTamd64", "NTARM64"));
        Assert.True(info.TargetsArchitecture(Architecture.Arm64));
        Assert.False(info.TargetsArchitecture(Architecture.X64));
    }

    // The same distribution ships a virtual *audio* driver. It is not Display class, so
    // the installer must never select it for a display device node.
    [Fact]
    public void Parse_AudioDriverInf_IsNotDisplayClass()
    {
        const string audio = """
            [Version]
            Signature="$Windows NT$"
            Class=MEDIA
            ClassGuid={4d36e96c-e325-11ce-bfc1-08002be10318}
            Provider=%MfgName%

            [Manufacturer]
            %MfgName%=VIRTUALAUDIODRIVER,NTamd64.10.0...22000

            [VIRTUALAUDIODRIVER.NTamd64.10.0...22000]
            %DeviceName% = Install, Root\VirtualAudioDriver

            [Strings]
            MfgName="VirtualAudio"
            DeviceName="Virtual Audio Driver"
            """;

        Assert.Equal("MEDIA", InfFile.Parse(audio).DeviceClass);
    }

    [Fact]
    public void TargetsArchitecture_UndecoratedInf_AppliesEverywhere()
    {
        const string inf = """
            [Manufacturer]
            %Mfg%=Models

            [Models]
            %Desc% = Install, Root\Device

            [Strings]
            Mfg = "Acme"
            Desc = "Device"
            """;

        var info = InfFile.Parse(inf);
        Assert.True(info.TargetsArchitecture(Architecture.X64));
        Assert.True(info.TargetsArchitecture(Architecture.Arm64));
    }
}
