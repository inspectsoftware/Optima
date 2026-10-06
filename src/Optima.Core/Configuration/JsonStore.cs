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
            return null;
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
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            return QuarantineOnCorrupt<T>(path, ex);
        }
    }

    public async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: path + ".bak");
            }
            else
            {
                File.Move(tmp, path);
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
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
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not quarantine corrupt file {Path}", path);
        }
    }
}
