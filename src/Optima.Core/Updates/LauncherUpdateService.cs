using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Optima.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Updates;

/// <summary>One published launcher release, parsed from GitHub's releases/latest.</summary>
public sealed record LauncherRelease(
    string TagName,
    Version Version,
    string ZipUrl,
    string ZipName,
    string NotesMarkdown,
    DateTimeOffset PublishedAt);

/// <summary>
/// Self-update against the project's GitHub releases, downloaded anonymously (which is why shipping this feature
/// required the repository to become public).
/// </summary>
public sealed class LauncherUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/inspectsoftware/Optima/releases/latest";

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<LauncherUpdateService> _logger;

    public LauncherUpdateService(AppPaths paths, ILogger<LauncherUpdateService> logger)
    {
        _paths = paths;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" + CurrentVersion.ToString(3));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion =>
        typeof(LauncherUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private string UpdatesRoot => Path.Combine(_paths.Root, "updates");

    private string PreviousDirectory => Path.Combine(UpdatesRoot, "previous");

    public bool RollbackAvailable => File.Exists(Path.Combine(PreviousDirectory, "Optima.exe"));

    public async Task<LauncherRelease?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Release check answered {Status}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseLatestRelease(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Release check failed");
            return null;
        }
    }

    public static LauncherRelease? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.GetString() is not { Length: > 0 } tag)
            {
                return null;
            }

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                return null;
            }

            string? zipUrl = null;
            string? zipName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && asset.TryGetProperty("browser_download_url", out var url))
                    {
                        zipUrl = url.GetString();
                        zipName = name;
                        break;
                    }
                }
            }
            if (zipUrl is null || zipName is null)
            {
                return null;
            }

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            var published = root.TryGetProperty("published_at", out var p)
                            && DateTimeOffset.TryParse(p.GetString(), out var at)
                ? at
                : DateTimeOffset.MinValue;

            return new LauncherRelease(tag, version, zipUrl, zipName, notes, published);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(LauncherRelease release) => release.Version > CurrentVersion;

    public async Task<string> DownloadAndStageAsync(LauncherRelease release, CancellationToken ct = default)
    {
        var stageRoot = Path.Combine(UpdatesRoot, release.TagName);
        var filesDir = Path.Combine(stageRoot, "files");
        Directory.CreateDirectory(stageRoot);

        var zipPath = Path.Combine(stageRoot, release.ZipName);
        await using (var target = File.Create(zipPath))
        await using (var source = await _http.GetStreamAsync(release.ZipUrl, ct).ConfigureAwait(false))
        {
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        if (Directory.Exists(filesDir))
        {
            Directory.Delete(filesDir, recursive: true);
        }
        ZipFile.ExtractToDirectory(zipPath, filesDir);
        if (!File.Exists(Path.Combine(filesDir, "Optima.exe")))
        {
            throw new InvalidOperationException("The downloaded package does not contain Optima.exe.");
        }
        _logger.LogInformation("Update {Tag} staged at {Dir}", release.TagName, filesDir);
        return filesDir;
    }

    public Task PrepareApplyAndLaunchSwapAsync(string stagedFilesDir, CancellationToken ct = default)
        => LaunchSwapAsync(stagedFilesDir, backupCurrent: true, ct);

    public Task LaunchRollbackAsync(CancellationToken ct = default)
        => LaunchSwapAsync(PreviousDirectory, backupCurrent: false, ct);

    private async Task LaunchSwapAsync(string sourceDir, bool backupCurrent, CancellationToken ct)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var probe = Path.Combine(installDir, ".optima-write-probe");
        await File.WriteAllTextAsync(probe, "probe", ct).ConfigureAwait(false);
        File.Delete(probe);

        if (backupCurrent)
        {
            if (Directory.Exists(PreviousDirectory))
            {
                Directory.Delete(PreviousDirectory, recursive: true);
            }
            CopyDirectory(installDir, PreviousDirectory);
        }

        var script = Path.Combine(UpdatesRoot, "apply.cmd");
        Directory.CreateDirectory(UpdatesRoot);
        await File.WriteAllTextAsync(script,
            """
            @echo off
            rem Optima update swap: waits for the app to exit, mirrors the staged build
            rem over the install folder, relaunches. Args: pid, sourceDir, installDir.
            :waitloop
            tasklist /FI "PID eq %~1" 2>nul | find "%~1" >nul && (timeout /t 1 /nobreak >nul & goto waitloop)
            robocopy "%~2" "%~3" /MIR /R:10 /W:1 >> "%~dp0apply.log" 2>&1
            start "" "%~3\Optima.exe"
            """, ct).ConfigureAwait(false);

        var pid = Environment.ProcessId;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\" {pid} \"{sourceDir}\" \"{installDir}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = UpdatesRoot,
        });
        _logger.LogInformation("Swap script launched ({Mode}); the app should now exit",
            backupCurrent ? "update" : "rollback");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.OrdinalIgnoreCase), overwrite: true);
        }
    }

    public void Dispose() => _http.Dispose();
}
