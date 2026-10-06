using System.Globalization;
using Optima.Core.Net;
using Microsoft.Extensions.Logging;

namespace Optima.Core.Stats;

/// <summary>Outcome of one profile lookup, so the UI can say why nothing showed.</summary>
public enum CopsLookupStatus
{
    /// <summary>A profile answered.</summary>
    Found,

    /// <summary>The API answered but there is no such player (the public API reports unknown players as HTTP 500).</summary>
    NotFound,

    /// <summary>The lookup could not be completed (offline, timeout, DNS, unexpected shape).</summary>
    Error,
}

/// <summary>One profile lookup with its outcome and any caveat worth showing the user.</summary>
public sealed record CopsLookupResult(
    CopsLookupStatus Status,
    CopsPlayerProfile? Profile,
    string Detail)
{
    public static readonly CopsLookupResult NotFoundPlayer =
        new(CopsLookupStatus.NotFound, null, "No player was found for that name or id.");

    public bool IsFound => Status == CopsLookupStatus.Found && Profile is not null;
}

/// <summary>
/// Read-only client for Critical Force's public profile API. Everything under <see cref="PublicRoot"/>
/// is the part of the game's API that third parties may read; everything else on that host is the
/// game's own, and asking it can cost a player their account. So this client cannot: every request
/// goes through <see cref="GetPublicAsync"/>, which refuses any address outside the public root.
/// </summary>
public sealed class CopsApiClient : IDisposable
{
    public const string PublicRoot = "https://default.prod.copsapi.criticalforce.fi/api/public/";
    private const string BaseUrl = PublicRoot;
    private readonly HttpClient _http;
    private readonly ILogger<CopsApiClient> _logger;

    public CopsApiClient(ILogger<CopsApiClient> logger)
        : this(logger, HttpPool.Shared, disposeHandler: false)
    {
    }

    // The handler overload exists for tests; production shares the app-wide connection pool.
    public CopsApiClient(ILogger<CopsApiClient> logger, HttpMessageHandler handler)
        : this(logger, handler, disposeHandler: true)
    {
    }

    private CopsApiClient(ILogger<CopsApiClient> logger, HttpMessageHandler handler, bool disposeHandler)
    {
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" + (typeof(CopsApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
    }

    /// <summary>Named lookup that reports why it failed instead of returning a bare null.</summary>
    public async Task<CopsLookupResult> LookupByNameAsync(string inGameName, CancellationToken ct = default)
    {
        var trimmed = inGameName.Trim();
        if (trimmed.Length == 0)
        {
            return CopsLookupResult.NotFoundPlayer;
        }

        var (profile, failure, problem) = await FetchAsync("profile?usernames=" + Uri.EscapeDataString(trimmed), ct).ConfigureAwait(false);
        if (profile is not null)
        {
            _logger.LogInformation("Profile lookup by name answered for {Name} (id {UserId})", profile.Name, profile.UserId);
            return new CopsLookupResult(CopsLookupStatus.Found, profile, "Found by in-game name.");
        }
        if (failure == CopsLookupStatus.Error)
        {
            _logger.LogDebug("Profile lookup by name failed for {Name}: {Problem}", trimmed, problem);
            return new CopsLookupResult(CopsLookupStatus.Error, null,
                "The stats API could not be reached (" + problem + "). Check the connection and try again.");
        }
        // The public API answers unknown players with HTTP 500, so a mistyped name is
        // indistinguishable from an API hiccup; say both, never pretend it worked.
        _logger.LogInformation("Profile lookup by name found nothing for {Name}", trimmed);
        return new CopsLookupResult(CopsLookupStatus.NotFound, null,
            "The API rejected the name lookup (" + problem + "). That usually means the name does not match any player, but it can also happen while the API is having trouble.");
    }

    /// <summary>Id lookup that reports why it failed instead of returning a bare null.</summary>
    public async Task<CopsLookupResult> LookupByIdAsync(long userId, CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return CopsLookupResult.NotFoundPlayer;
        }

        var (profile, failure, problem) = await FetchAsync("profile?ids=" + userId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (profile is not null)
        {
            _logger.LogInformation("Profile lookup by id answered for {Name} (id {UserId})", profile.Name, profile.UserId);
            if (LooksLikePlaceholderProfile(profile))
            {
                return new CopsLookupResult(CopsLookupStatus.Found, profile,
                    "The id resolved to a default-named account with no stats. Either the id is wrong or the account is brand new; the in-game name is the safer identifier.");
            }
            return new CopsLookupResult(CopsLookupStatus.Found, profile, "Found by account id.");
        }
        if (failure == CopsLookupStatus.Error)
        {
            _logger.LogDebug("Profile lookup by id failed for {UserId}: {Problem}", userId, problem);
            return new CopsLookupResult(CopsLookupStatus.Error, null,
                "The stats API could not be reached (" + problem + "). Check the connection and try again.");
        }
        _logger.LogInformation("Profile lookup by id found nothing for {UserId}", userId);
        return new CopsLookupResult(CopsLookupStatus.NotFound, null,
            "The API rejected the id lookup (" + problem + ").");
    }

    /// <summary>
    /// Resolves the player from the Settings pair: the account id first (exact, survives renames),
    /// then the in-game name when the id is unset or missed.
    /// </summary>
    public async Task<CopsLookupResult> LookupPlayerAsync(string? inGameName, long? accountId, CancellationToken ct = default)
    {
        var ign = inGameName?.Trim() ?? string.Empty;
        if (accountId is > 0)
        {
            var byId = await LookupByIdAsync(accountId.Value, ct).ConfigureAwait(false);
            if (byId.IsFound)
            {
                // A placeholder name with a different in-game name on file usually means a wrong id.
                if (ign.Length > 0
                    && byId.Profile is { } profile
                    && LooksLikePlaceholderProfile(profile)
                    && !string.Equals(profile.Name, ign, StringComparison.OrdinalIgnoreCase))
                {
                    var byName = await LookupByNameAsync(ign, ct).ConfigureAwait(false);
                    if (byName.IsFound)
                    {
                        return new CopsLookupResult(CopsLookupStatus.Found, byName.Profile,
                            "Found by in-game name. The account id did not match this player, so it is probably wrong; the id is kept only as a fallback.");
                    }
                    return byId;
                }
                if (ign.Length > 0
                    && byId.Profile is { } named
                    && !string.Equals(named.Name, ign, StringComparison.OrdinalIgnoreCase))
                {
                    return byId with
                    {
                        Detail = "Found by account id. The profile's current name is " + named.Name + ", which differs from the in-game name saved in Settings (the player may have renamed).",
                    };
                }
                return byId;
            }
            if (byId.Status == CopsLookupStatus.Error || ign.Length == 0)
            {
                // A real outage is reported as such; only a clean miss falls through to the name.
                return byId;
            }
        }

        if (ign.Length > 0)
        {
            return await LookupByNameAsync(ign, ct).ConfigureAwait(false);
        }

        return CopsLookupResult.NotFoundPlayer;
    }

    /// <summary>
    /// The public API fabricates a scaffold profile (default name derived from the queried id, all
    /// stats zero) for unknown ids. Detect it so a typo'd id never masquerades as real stats.
    /// </summary>
    public static bool LooksLikePlaceholderProfile(CopsPlayerProfile profile)
        => profile.Name.StartsWith("OPS-", StringComparison.Ordinal)
            && string.Equals(profile.Name, "OPS-" + profile.UserId.ToString("D9", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && profile.Seasons.All(s => s.Ranked.IsZero && s.Casual.IsZero && s.Custom.IsZero);

    /// <summary>Whether an address is inside the public API. "../" segments are resolved before the test.</summary>
    public static bool IsPublicEndpoint(Uri uri)
        => uri.IsAbsoluteUri && uri.AbsoluteUri.StartsWith(PublicRoot, StringComparison.Ordinal);

    /// <summary>The only way a request leaves this client.</summary>
    private Task<HttpResponseMessage> GetPublicAsync(string relativeUrl, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress!, relativeUrl);
        if (!IsPublicEndpoint(uri))
        {
            throw new InvalidOperationException($"Refused: {uri.AbsolutePath} is outside the public Critical Ops API.");
        }
        return _http.GetAsync(uri, ct);
    }

    private async Task<(CopsPlayerProfile? Profile, CopsLookupStatus Failure, string Problem)> FetchAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await GetPublicAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Profile lookup answered {Status}", (int)response.StatusCode);
                return (null, CopsLookupStatus.NotFound, "HTTP " + (int)response.StatusCode);
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var profile = CopsProfileParser.Parse(json);
            return profile is not null
                ? (profile, default(CopsLookupStatus), string.Empty)
                : (null, CopsLookupStatus.NotFound, "the answer was not a profile");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Profile lookup failed");
            return (null, CopsLookupStatus.Error, "network error");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Profile lookup timed out");
            return (null, CopsLookupStatus.Error, "timed out");
        }
    }

    // Disposing the client is safe with the shared handler: it was created with disposeHandler: false.
    public void Dispose() => _http.Dispose();
}
