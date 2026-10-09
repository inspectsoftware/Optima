using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Configuration;

/// <summary>
/// Crash-safe JSON persistence: writes go to a temp file, then replace the target atomically, so a crash mid-write
/// never corrupts settings or recovery snapshots.
/// </summary>
public sealed class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<JsonStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonStore(ILogger<JsonStore> logger)
    {
        _logger = logger;
    }

    public async Task<T?> LoadAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        if (!File.Exists(path))
        {
            return RecoverMissing<T>(path);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return QuarantineOnCorrupt<T>(path, ex);
        }
    }

    /// <summary>
    /// Reads a file Optima does not own (one the user picked). Unlike <see cref="LoadAsync"/> a file
    /// that does not parse is left exactly where it is.
    /// </summary>
    public async Task<T?> ReadExternalAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Path} is not valid JSON for {Type}", path, typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// Synchronous counterpart to <see cref="LoadAsync"/> for startup paths that must complete
    /// before the first frame is painted.
    /// </summary>
    public T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return RecoverMissing<T>(path);
        }

        // This is the read the start waits on, and a scanner holding the file for a moment is the
        // same passing thing here as it is for a save. Unretried, it was the app refusing to start.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            }
            catch (JsonException ex)
            {
                return QuarantineOnCorrupt<T>(path, ex);
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>
    /// No file, but a backup of it. A replace is two renames, and a process that ends between them
    /// leaves exactly this: the last good generation under the backup's name and nothing under the
    /// file's own. Read as "no file", that was every setting and profile silently back to defaults.
    /// A file removed on purpose has no backup; <see cref="Delete"/> takes it first.
    /// </summary>
    private T? RecoverMissing<T>(string path) where T : class
    {
        var backup = path + ".bak";
        var recovered = ReadBackup<T>(backup);
        if (recovered is null)
        {
            return null;
        }

        try
        {
            File.Copy(backup, path, overwrite: false);
        }
        catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(copy, "Could not put the backup back in place at {Path}", path);
        }
        _logger.LogWarning("{Path} was missing; recovered from the backup of the previous save", path);
        return recovered;
    }

    public async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            // The dispose is awaited off the caller's context too. A small file fits the stream's
            // buffer, so the flush in the dispose was the first await to wait, and the rename and
            // the gate's release then ran on the UI thread whenever that was the caller.
            var stream = File.Create(tmp);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
                // Onto the disk before the rename. The rename is journaled and the contents are
                // not: after a power cut or a blue screen the new name could point at an empty file.
                stream.Flush(flushToDisk: true);
            }

            // A scanner or the indexer can hold the file or its backup open for a moment right
            // after the previous save. That passes by itself, and a save that gave up on it took
            // the app down from whichever command had asked for it.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tmp, path, destinationBackupFileName: path + ".bak");
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100 * attempt, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Delete(string path)
    {
        try
        {
            // The backup goes with it, and goes first. Left behind, it is an older generation that
            // a later read would hand back as recovered: for the recovery snapshot, another session's.
            if (File.Exists(path + ".bak"))
            {
                File.Delete(path + ".bak");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    /// <summary>
    /// A file that does not parse is set aside, and its previous saved generation takes its place
    /// when there is one. It has to happen here, at the read: every save rotates the backup, so the
    /// second save after a silent fall back to defaults would overwrite the last good copy with
    /// those defaults, and by the time anyone looked there would be nothing left to restore.
    /// </summary>
    private T? QuarantineOnCorrupt<T>(string path, JsonException ex) where T : class
    {
        var backup = path + ".bak";
        var recovered = ReadBackup<T>(backup);
        TryQuarantine(path);
        if (recovered is null)
        {
            _logger.LogError(ex, "Corrupt JSON at {Path}; renaming aside and using defaults", path);
            return null;
        }

        try
        {
            File.Copy(backup, path, overwrite: true);
        }
        catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
        {
            // The values are in memory and the next save writes them; only the file is late.
            _logger.LogDebug(copy, "Could not put the backup back in place at {Path}", path);
        }
        _logger.LogWarning(ex, "Corrupt JSON at {Path}; recovered from the backup of the previous save", path);
        return recovered;
    }

    private static T? ReadBackup<T>(string backup) where T : class
    {
        try
        {
            return File.Exists(backup) ? JsonSerializer.Deserialize<T>(File.ReadAllText(backup), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryQuarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not quarantine corrupt file {Path}", path);
        }
    }
}
