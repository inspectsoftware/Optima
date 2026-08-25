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
}
