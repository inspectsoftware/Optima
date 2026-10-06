using System.Text.Json;
using Optima.Core.Net;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Discord;

/// <summary>
/// Finds out which rich-presence art a Discord application actually has.
///
/// Discord renders the card's large image from an asset uploaded to the application, and an asset key
/// that does not exist comes out as the "missing image" question mark - not as an error anything here
/// can see. Uploading is a manual step in the developer portal, so the presence asks Discord what is
/// really there instead of assuming the upload happened, and falls back to a public artwork URL until
/// the application has something of its own.
/// </summary>
public sealed class DiscordArtCatalog : IDisposable
{
    private const string AssetsUrl = "https://discord.com/api/v10/oauth2/applications/{0}/assets";

    private readonly HttpClient _http;
    private readonly ILogger<DiscordArtCatalog> _logger;

    public DiscordArtCatalog(ILogger<DiscordArtCatalog> logger)
        : this(logger, HttpPool.Shared, disposeHandler: false)
    {
    }

    // The handler overload exists for tests; production shares the app-wide connection pool.
    public DiscordArtCatalog(ILogger<DiscordArtCatalog> logger, HttpMessageHandler handler)
        : this(logger, handler, disposeHandler: true)
    {
    }

    private DiscordArtCatalog(ILogger<DiscordArtCatalog> logger, HttpMessageHandler handler, bool disposeHandler)
    {
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" +
            (typeof(DiscordArtCatalog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
    }

    /// <summary>
    /// Every asset the application has uploaded, in one answer. The card needs more than one key
    /// (the app mark and a rank emblem), and a single request answers both: asking per key would
    /// multiply the same call and let the two answers disagree.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, ulong Id)>> FindAssetsAsync(
        string applicationId, CancellationToken ct = default)
    {
        if (applicationId.Length == 0)
        {
            return [];
        }

        try
        {
            using var response = await _http.GetAsync(string.Format(AssetsUrl, applicationId), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Discord art asset lookup answered {Status}", (int)response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseAssets(json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Discord art asset lookup failed");
            return [];
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Discord art asset lookup did not answer");
            return [];
        }
    }

    /// <summary>The asset names and ids in one answer from Discord; anything unreadable is skipped.</summary>
    public static IReadOnlyList<(string Name, ulong Id)> ParseAssets(string json)
    {
        var assets = new List<(string Name, ulong Id)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var value = name.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var id = item.TryGetProperty("id", out var raw) && raw.ValueKind == JsonValueKind.String
                && ulong.TryParse(raw.GetString(), out var parsedId)
                    ? parsedId
                    : 0;
            assets.Add((value, id));
        }
        return assets;
    }

    /// <summary>
    /// Which uploaded asset to use: the one named after the artwork, else the most recently uploaded.
    /// Asset ids are snowflakes, so the largest is the newest. Separators and case are ignored when
    /// matching names, because the portal rewrites both.
    /// </summary>
    public static string? Pick(IReadOnlyList<(string Name, ulong Id)> assets, string preferredKey)
    {
        if (assets.Count == 0)
        {
            return null;
        }

        var wanted = Normalize(preferredKey);
        var named = assets.FirstOrDefault(a => string.Equals(Normalize(a.Name), wanted, StringComparison.Ordinal));
        return named.Name ?? assets.OrderByDescending(a => a.Id).First().Name;
    }

    /// <summary>
    /// The asset uploaded under exactly this name, or null.
    ///
    /// Deliberately strict, unlike <see cref="Pick"/>: a missing app mark can fall back to the newest
    /// upload and still look right, but a missing rank emblem must not. Falling back there would pin
    /// one player's rank art onto everybody's card, which is worse than showing no emblem at all.
    /// </summary>
    public static string? Match(IReadOnlyList<(string Name, ulong Id)> assets, string wantedKey)
    {
        var wanted = Normalize(wantedKey);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var asset in assets)
        {
            if (string.Equals(Normalize(asset.Name), wanted, StringComparison.Ordinal))
            {
                return asset.Name;
            }
        }
        return null;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Safe with the shared handler: it was created with disposeHandler: false.
    public void Dispose() => _http.Dispose();
}
