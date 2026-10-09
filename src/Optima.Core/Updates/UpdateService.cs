using System.Security.Cryptography;
using System.Text.Json;
using Optima.Core.Configuration;
using Optima.Core.Net;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Updates;

/// <summary>One published release that this build can install: a setup and the signature that goes with it.</summary>
public sealed record UpdateRelease(Version Version, string SetupName, string SetupUrl, string SignatureUrl, long SetupSize)
{
    public string VersionText => UpdateSignature.Three(Version);
}

public enum UpdateCheckStatus
{
    /// <summary>The latest release is this build, or older.</summary>
    UpToDate,

    /// <summary>A newer release is there and can be installed.</summary>
    Available,

    /// <summary>The check could not be completed (offline, GitHub answered with an error).</summary>
    Failed,
}

/// <summary>What a check found, with a sentence that can be shown as it is.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string Detail);

/// <summary>A downloaded and verified setup, or the reason there is none.</summary>
public sealed record UpdateDownloadResult(string? SetupPath, string Detail)
{
    public bool Ok => SetupPath is not null;
}

/// <summary>
/// Looks for a newer Optima on the project's GitHub releases, and fetches and verifies its setup.
/// Installing is the caller's part: it means starting the setup and closing the app.
///
/// Nothing here throws to the caller, and nothing that was downloaded is handed back unless
/// <see cref="UpdateSignature"/> says it is Optima's.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string ReleasesPage = "https://github.com/inspectsoftware/Optima/releases";
    private const string LatestReleaseUrl = "https://api.github.com/repos/inspectsoftware/Optima/releases/latest";
    private const string DownloadRoot = "https://github.com/inspectsoftware/Optima/releases/download/";

    /// <summary>A setup is about 60 MB. Anything far beyond that is not one, and is not written to disk.</summary>
    private const long MaxSetupBytes = 400L * 1024 * 1024;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly string _publicKey;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(AppPaths paths, ILogger<UpdateService> logger)
        : this(paths, logger, HttpPool.Shared, disposeHandler: false, UpdateSignature.PublicKey)
    {
    }

    // The handler and key overload exists for tests; production shares the app-wide connection
    // pool and trusts the one key this build was compiled with.
    public UpdateService(AppPaths paths, ILogger<UpdateService> logger, HttpMessageHandler handler, string publicKey)
        : this(paths, logger, handler, disposeHandler: true, publicKey)
    {
    }

    private UpdateService(
        AppPaths paths, ILogger<UpdateService> logger, HttpMessageHandler handler, bool disposeHandler, string publicKey)
    {
        _logger = logger;
        _publicKey = publicKey;
        _folder = Path.Combine(paths.Root, "updates");
        // No timeout of its own: a check and a download want very different ones, set per call.
        _http = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" + UpdateSignature.Three(CurrentVersion));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(CheckTimeout);
            using var response = await _http.GetAsync(LatestReleaseUrl, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check answered {Status}", (int)response.StatusCode);
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"GitHub answered the update check with {(int)response.StatusCode}.");
            }

            var release = ParseLatestRelease(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            if (release is null || !IsNewer(release.Version, CurrentVersion))
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null,
                    $"Optima {UpdateSignature.Three(CurrentVersion)} is the latest version.");
            }

            _logger.LogInformation("Optima {Version} is available", release.VersionText);
            return new UpdateCheckResult(UpdateCheckStatus.Available, release, $"Optima {release.VersionText} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "The update check was cancelled.");
            }
            _logger.LogDebug(ex, "Update check failed");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub could not be reached for the update check.");
        }
    }

    /// <summary>
    /// The latest release as something this build can install, or null: a tag that is not a plain
    /// version (previews are tagged v0.7.0-preview.1 and are never offered), a release without its
    /// setup, without the setup's signature, or with either of them somewhere other than this
    /// project's own release downloads.
    /// </summary>
    public static UpdateRelease? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.GetString() is not { Length: > 0 } tag
                || !Version.TryParse(tag.TrimStart('v', 'V'), out var version)
                || !root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var setupName = $"Optima-Setup-{UpdateSignature.Three(version)}.exe";
            string? setupUrl = null, signatureUrl = null;
            long size = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null || !url.StartsWith(DownloadRoot, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(name, setupName, StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = url;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (string.Equals(name, setupName + ".sig", StringComparison.OrdinalIgnoreCase))
                {
                    signatureUrl = url;
                }
            }

            return setupUrl is null || signatureUrl is null || size > MaxSetupBytes
                ? null
                : new UpdateRelease(version, setupName, setupUrl, signatureUrl, size);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Compared on three parts: the running build is 0.8.0.0 and a tag is 0.8.0, and those are the same version.</summary>
    public static bool IsNewer(Version release, Version current)
        => new Version(release.Major, release.Minor, Math.Max(release.Build, 0))
            > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>
    /// Fetches the setup and its signature and checks one against the other. The file only gets
    /// its .exe name once it has passed, so nothing unverified in the folder can be started by
    /// mistake, and a download that fails or does not verify is deleted.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateRelease release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var target = Path.Combine(_folder, release.SetupName);
        var partial = target + ".part";
        try
        {
            Directory.CreateDirectory(_folder);
            // Earlier downloads, whatever became of them. One setup at a time is all there ever is.
            foreach (var old in Directory.EnumerateFiles(_folder))
            {
                File.Delete(old);
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(DownloadTimeout);

            var signature = await _http.GetStringAsync(release.SignatureUrl, limit.Token).ConfigureAwait(false);

            byte[] hash;
            using (var response = await _http.GetAsync(
                release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var expected = response.Content.Headers.ContentLength ?? release.SetupSize;
                if (expected > MaxSetupBytes)
                {
                    return new UpdateDownloadResult(null, "The download is far too large to be an Optima setup, so it was not fetched.");
                }

                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var file = File.Create(partial);
                    await using (file.ConfigureAwait(false))
                    {
                        var buffer = new byte[128 * 1024];
                        long total = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxSetupBytes)
                            {
                                throw new InvalidDataException("The download kept growing past the size of any Optima setup.");
                            }
                            sha.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), limit.Token).ConfigureAwait(false);
                            if (expected > 0)
                            {
                                progress?.Report(Math.Min(1.0, (double)total / expected));
                            }
                        }
                    }
                }
                hash = sha.GetHashAndReset();
            }

            if (!UpdateSignature.Verify(hash, release.Version, signature, _publicKey))
            {
                File.Delete(partial);
                _logger.LogError("The downloaded setup for {Version} does not carry Optima's signature", release.VersionText);
                return new UpdateDownloadResult(null,
                    "The downloaded setup is not signed by Optima's key, so it was deleted and not run.");
            }

            File.Move(partial, target, overwrite: true);
            _logger.LogInformation("Update {Version} downloaded and verified", release.VersionText);
            return new UpdateDownloadResult(target, "Downloaded and verified.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
            or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(partial);
            _logger.LogWarning(ex, "Downloading the update failed");
            return new UpdateDownloadResult(null, ct.IsCancellationRequested
                ? "The download was cancelled."
                : "The update could not be downloaded: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
