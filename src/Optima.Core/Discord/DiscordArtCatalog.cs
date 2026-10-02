using System.Net.Http;
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
    /// The asset key to use, or null when the application has nothing uploaded, in which case the
    /// caller shows the public artwork rather than a broken image.
    /// </summary>
    public async Task<string?> FindKeyAsync(string applicationId, string preferredKey, CancellationToken ct = default)
    {
        if (applicationId.Length == 0)
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(string.Format(AssetsUrl, applicationId), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Discord art asset lookup answered {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Pick(ParseAssets(json), preferredKey);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Discord art asset lookup failed");
            return null;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Discord art asset lookup did not answer");
            return null;
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

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Safe with the shared handler: it was created with disposeHandler: false.
    public void Dispose() => _http.Dispose();
}
