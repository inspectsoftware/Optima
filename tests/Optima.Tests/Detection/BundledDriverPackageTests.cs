using System.Runtime.InteropServices;
using System.Text;
using Optima.Core.Detection;
using Xunit;

namespace Optima.Tests.Detection;

/// <summary>
/// The bundled folder can hold several INFs: one per architecture, plus drivers that are not
/// displays at all. What gets installed must be the Display-class package for the machine's own
/// architecture, and nothing at all when only a foreign architecture is present (installing the
/// arm64 package on x64 is how a "the driver will not install" report is born).
/// </summary>
public class BundledDriverPackageTests : IDisposable
{
    // Shape of the shipped MttVDD.inf: tokenized strings, a decorated Models section, and a
    // root-enumerated hardware id.
    private const string X64Inf = """
        [Version]
        Signature="$Windows NT$"
        Class = Display
        ClassGuid = {4D36E968-E325-11CE-BFC1-08002BE10318}
        Provider=%ManufacturerName%
        CatalogFile=MttVDD.cat
        DriverVer = 12/24/2024,11.30.4.434

        [Manufacturer]
        %ManufacturerName%=Standard,NTamd64

        [Standard.NTamd64]
        %DeviceName%=MyDevice_Install, Root\MttVDD

        [Strings]
        ManufacturerName="MikeTheTech"
        DeviceName="Virtual Display Driver"
        """;

    private static readonly string Arm64Inf = X64Inf.Replace("NTamd64", "NTARM64");

    // The upstream distribution also ships a virtual audio driver. It is not Display class, so
    // it must never be chosen for the display device node the installer creates.
    private const string AudioInf = """
        [Version]
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-driver-" + Guid.NewGuid().ToString("N"));

    public BundledDriverPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Find_PicksThePackageForTheRequestedArchitecture()
    {
        var x64 = WriteInf("x64", X64Inf);
        var arm64 = WriteInf("arm64", Arm64Inf);

        Assert.Equal(x64, BundledDriverPackage.Find(_root, Architecture.X64)?.InfPath);
        Assert.Equal(arm64, BundledDriverPackage.Find(_root, Architecture.Arm64)?.InfPath);
    }

    [Fact]
    public void Find_ReadsThePackageFacts()
    {
        WriteInf("x64", X64Inf);
        WriteCatalog("x64");

        var package = BundledDriverPackage.Find(_root, Architecture.X64);

        Assert.NotNull(package);
        Assert.Equal(@"Root\MttVDD", package!.HardwareId);
        Assert.Equal("Virtual Display Driver", package.DisplayName);
        Assert.Equal("MikeTheTech", package.Provider);
        Assert.True(package.HasCatalog);
    }

    [Fact]
    public void Find_PackageWithoutCatalog_ReportsIt()
    {
        WriteInf("x64", X64Inf);

        Assert.False(BundledDriverPackage.Find(_root, Architecture.X64)!.HasCatalog);
    }

    // The real bundled INF is UTF-16 with a byte order mark, which is legal for INF files and
    // must not turn into "no driver package ships with this build".
    [Fact]
    public void Find_ReadsUtf16InfFiles()
    {
        var path = WriteInf("x64", X64Inf, Encoding.Unicode);

        var package = BundledDriverPackage.Find(_root, Architecture.X64);

        Assert.Equal(path, package?.InfPath);
        Assert.Equal(@"Root\MttVDD", package?.HardwareId);
    }

    [Fact]
    public void Find_WithoutAPackageForThisArchitecture_InstallsNothing()
    {
        WriteInf("arm64", Arm64Inf);

        var notes = new List<string>();

        Assert.Null(BundledDriverPackage.Find(_root, Architecture.X64, notes.Add));
        Assert.Contains(notes, note => note.Contains("none targets X64"));
    }

    [Fact]
    public void Find_IgnoresDriversThatAreNotDisplays()
    {
        WriteInf("x64", AudioInf);

        var notes = new List<string>();

        Assert.Null(BundledDriverPackage.Find(_root, Architecture.X64, notes.Add));
        Assert.Contains(notes, note => note.Contains("not Display"));
    }

    [Fact]
    public void Find_MissingFolder_ReportsNoPackage()
    {
        Assert.Null(BundledDriverPackage.Find(Path.Combine(_root, "absent"), Architecture.X64));
    }

    // Smoke test against the package that actually ships: catches a corrupted or unexpectedly
    // shaped INF in the repository before it reaches an installer.
    [Fact]
    public void Find_ShippedDriverFolder_YieldsAnInstallablePackage()
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            return;
        }

        var package = BundledDriverPackage.Find(Path.Combine(repoRoot, "drivers", "VirtualDisplayDriver"));

        Assert.NotNull(package);
        Assert.Equal(@"Root\MttVDD", package!.HardwareId);
        Assert.True(package.HasCatalog);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "drivers", "VirtualDisplayDriver")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private string WriteInf(string relativeFolder, string text, Encoding? encoding = null)
    {
        return WriteFile(relativeFolder, "MttVDD.inf", text, encoding);
    }

    private void WriteCatalog(string relativeFolder)
        => WriteFile(relativeFolder, "MttVDD.cat", "signed", null);

    private string WriteFile(string relativeFolder, string fileName, string text, Encoding? encoding)
    {
        var folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
