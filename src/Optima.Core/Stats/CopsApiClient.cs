using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Stats;

/// <summary>Read-only client for Critical Force's public profile API.</summary>
public sealed class CopsApiClient : IDisposable
{
    private const string BaseUrl = "https://default.prod.copsapi.criticalforce.fi/api/public/";
    private readonly HttpClient _http;
    private readonly ILogger<CopsApiClient> _logger;

    public CopsApiClient(ILogger<CopsApiClient> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" + (typeof(CopsApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
    }

    public Task<CopsPlayerProfile?> GetProfileByNameAsync(string inGameName, CancellationToken ct = default)
        => GetProfileAsync("profile?usernames=" + Uri.EscapeDataString(inGameName.Trim()), ct);

    public Task<CopsPlayerProfile?> GetProfileByIdAsync(long userId, CancellationToken ct = default)
        => GetProfileAsync("profile?ids=" + userId, ct);

    private async Task<CopsPlayerProfile?> GetProfileAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Profile lookup answered {Status}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return CopsProfileParser.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Profile lookup failed");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
