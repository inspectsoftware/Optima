using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Optima.Core.Configuration;
using Optima.Core.Updates;
using Optima.Tests.Stats;
using Xunit;

namespace Optima.Tests.Updates;

/// <summary>
/// The update check decides what is offered, and the signature decides what is run as
/// administrator. Both are pinned here without a network: the release answer is GitHub's shape,
/// and the key is one made for the test.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string Download = "https://github.com/inspectsoftware/Optima/releases/download/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "optima-updates-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private static string ReleaseJson(string tag, params string[] assetUrls)
        => "{\"tag_name\":\"" + tag + "\",\"prerelease\":false,\"assets\":["
            + string.Join(",", assetUrls.Select(url =>
                "{\"name\":\"" + url[(url.LastIndexOf('/') + 1)..] + "\",\"size\":1234,\"browser_download_url\":\"" + url + "\"}"))
            + "]}";

    private static string[] SetupAndSignature(string version)
        => [$"{Download}v{version}/Optima-Setup-{version}.exe", $"{Download}v{version}/Optima-Setup-{version}.exe.sig"];

    [Fact]
    public void AReleaseWithItsSetupAndSignatureIsOffered()
    {
        var release = UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0", SetupAndSignature("0.9.0")));

        Assert.NotNull(release);
        Assert.Equal("0.9.0", release.VersionText);
        Assert.Equal("Optima-Setup-0.9.0.exe", release.SetupName);
        Assert.EndsWith(".exe.sig", release.SignatureUrl);
    }

    [Fact]
    public void ReleasesThatCannotBeInstalledSafelyAreNotOffered()
    {
        // No signature: there is nothing to check the setup against.
        Assert.Null(UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0", SetupAndSignature("0.9.0")[0])));
        // A preview tag is not a version this build would move to by itself.
        Assert.Null(UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0-preview.1", SetupAndSignature("0.9.0"))));
        // The portable zip alone is not a setup.
        Assert.Null(UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0", Download + "v0.9.0/Optima-v0.9.0-win-x64.zip")));
        // A setup that lives anywhere but this project's own release downloads.
        Assert.Null(UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0",
            "https://example.com/Optima-Setup-0.9.0.exe", "https://example.com/Optima-Setup-0.9.0.exe.sig")));
        // A setup for another version under this tag.
        Assert.Null(UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0", SetupAndSignature("0.8.0"))));
        Assert.Null(UpdateService.ParseLatestRelease("not json"));
        Assert.Null(UpdateService.ParseLatestRelease("[]"));
    }

    [Theory]
    [InlineData("0.8.1", "0.8.0.0", true)]
    [InlineData("0.9", "0.8.5.0", true)]
    [InlineData("0.8.0", "0.8.0.0", false)]
    [InlineData("0.7.9", "0.8.0.0", false)]
    public void NewerMeansAHigherThreePartVersion(string release, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(Version.Parse(release), Version.Parse(current)));

    [Fact]
    public void ASignatureHoldsForExactlyTheSetupAndVersionItWasMadeFor()
    {
        var hash = SHA256.HashData("the setup"u8);
        var version = new Version(0, 9, 0);
        var signature = UpdateSignature.Sign(hash, version, _key);

        Assert.True(UpdateSignature.Verify(hash, version, signature, PublicKey));
        // The running build spells its version with four parts; it is the same version.
        Assert.True(UpdateSignature.Verify(hash, new Version(0, 9, 0, 0), signature, PublicKey));

        Assert.False(UpdateSignature.Verify(SHA256.HashData("another file"u8), version, signature, PublicKey));
        // An old setup with its real signature, offered again as something newer.
        Assert.False(UpdateSignature.Verify(hash, new Version(0, 9, 1), signature, PublicKey));
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.Verify(hash, version, signature, Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo())));
        Assert.False(UpdateSignature.Verify(hash, version, "not a signature", PublicKey));
        Assert.False(UpdateSignature.Verify(hash, version, signature, "not a key"));
    }

    [Fact]
    public void TheKeyInThisBuildIsAUsablePublicKey()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }

    private UpdateService Service(string setupBody, string signature)
        => new(new AppPaths(_root), NullLogger<UpdateService>.Instance,
            new FakeHandler(request => (HttpStatusCode.OK,
                request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? signature : setupBody)),
            PublicKey);

    private UpdateRelease Release()
        => UpdateService.ParseLatestRelease(ReleaseJson("v0.9.0", SetupAndSignature("0.9.0")))!;

    private string SignatureFor(string setupBody)
        => UpdateSignature.Sign(SHA256.HashData(Encoding.UTF8.GetBytes(setupBody)), new Version(0, 9, 0), _key);

    [Fact]
    public async Task AVerifiedDownloadIsHandedBackUnderItsSetupName()
    {
        using var service = Service("the setup", SignatureFor("the setup"));

        var result = await service.DownloadAsync(Release());

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("Optima-Setup-0.9.0.exe", Path.GetFileName(result.SetupPath));
        Assert.Equal("the setup", File.ReadAllText(result.SetupPath!));
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatWasSignedIsDeletedAndNotHandedBack()
    {
        using var service = Service("something else entirely", SignatureFor("the setup"));

        var result = await service.DownloadAsync(Release());

        Assert.False(result.Ok);
        Assert.Contains("not signed", result.Detail);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "updates")));
    }

    [Fact]
    public async Task AnOutageIsAnAnswerAndNotAnException()
    {
        using var service = new UpdateService(
            new AppPaths(_root), NullLogger<UpdateService>.Instance, FakeHandler.AlwaysThrows(), PublicKey);

        Assert.Equal(UpdateCheckStatus.Failed, (await service.CheckAsync()).Status);
        Assert.False((await service.DownloadAsync(Release())).Ok);
    }

    [Fact]
    public async Task TheCheckOffersOnlyWhatIsNewerThanThisBuild()
    {
        using var older = new UpdateService(new AppPaths(_root), NullLogger<UpdateService>.Instance,
            new FakeHandler(_ => (HttpStatusCode.OK, ReleaseJson("v0.0.1", SetupAndSignature("0.0.1")))), PublicKey);
        using var newer = new UpdateService(new AppPaths(_root), NullLogger<UpdateService>.Instance,
            new FakeHandler(_ => (HttpStatusCode.OK, ReleaseJson("v99.0.0", SetupAndSignature("99.0.0")))), PublicKey);

        Assert.Equal(UpdateCheckStatus.UpToDate, (await older.CheckAsync()).Status);
        var found = await newer.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Available, found.Status);
        Assert.Equal("99.0.0", found.Release!.VersionText);
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
        {
        }
    }
}
