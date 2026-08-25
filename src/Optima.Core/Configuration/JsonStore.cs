using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Configuration;

/// <summary>
/// Crash-safe JSON persistence: writes go to a temp file, then replace the target atomically,
/// so a crash mid-write never corrupts settings or recovery snapshots.
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
            _logger.LogError(ex, "Corrupt JSON at {Path}; renaming aside and using defaults", path);
            TryQuarantine(path);
            return null;
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
                File.Replace(tmp, path, destinationBackupFileName: null);
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
