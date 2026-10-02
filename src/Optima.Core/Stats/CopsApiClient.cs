using System.Globalization;
using System.Net.Http;
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

/// <summary>Read-only client for Critical Force's public profile API.</summary>
public sealed class CopsApiClient : IDisposable
{
    private const string BaseUrl = "https://default.prod.copsapi.criticalforce.fi/api/public/";
    // A leaderboard page is re-read on every tab switch and the API has no server-side filtering, so
    // a fresh copy of a page answers the next switch without a request. The refresh button and the
    // five-minute timer both pass refresh: true.
    private static readonly TimeSpan LeaderboardCacheLifetime = TimeSpan.FromSeconds(90);

    private readonly HttpClient _http;
    private readonly ILogger<CopsApiClient> _logger;

    private readonly Dictionary<string, (DateTimeOffset At, object Rows)> _leaderboardCache = new(StringComparer.Ordinal);
    private readonly object _leaderboardCacheGate = new();

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

    public async Task<CopsPlayerProfile?> GetProfileByNameAsync(string inGameName, CancellationToken ct = default)
        => (await LookupByNameAsync(inGameName, ct).ConfigureAwait(false)).Profile;

    public async Task<CopsPlayerProfile?> GetProfileByIdAsync(long userId, CancellationToken ct = default)
        => (await LookupByIdAsync(userId, ct).ConfigureAwait(false)).Profile;

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

    /// <summary>Batch profile lookup by exact account ids (the API accepts comma-separated ids).</summary>
    public async Task<IReadOnlyList<CopsPlayerProfile>> GetProfilesByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var list = ids.Where(id => id > 0).Distinct().Take(50).ToList();
        if (list.Count == 0)
        {
            return [];
        }
        var query = "profile?ids=" + string.Join(",", list.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var (profiles, _, _) = await FetchRawAsync(query, ct).ConfigureAwait(false);
        return profiles;
    }

    /// <summary>Batch profile lookup by exact in-game names (the API accepts comma-separated names).</summary>
    public async Task<IReadOnlyList<CopsPlayerProfile>> GetProfilesByNamesAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var list = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (list.Count == 0)
        {
            return [];
        }
        var query = "profile?usernames=" + string.Join(",", list.Select(Uri.EscapeDataString));
        var (profiles, _, _) = await FetchRawAsync(query, ct).ConfigureAwait(false);
        return profiles;
    }

    /// <summary>
    /// One leaderboard page (elite / ranked / kills / clan). No server-side filtering exists, so
    /// callers cache the answer briefly and pass <paramref name="refresh"/> to bypass it.
    /// </summary>
    public async Task<(IReadOnlyList<T> Rows, string? Problem)> GetLeaderboardAsync<T>(
        string endpoint, Func<string, IReadOnlyList<T>> parse, CancellationToken ct = default, bool refresh = false)
    {
        if (!refresh && TryGetCachedPage<T>(endpoint) is { } cached)
        {
            return (cached, null);
        }

        try
        {
            using var response = await _http.GetAsync("../leaderboard/" + endpoint, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ([], "HTTP " + (int)response.StatusCode);
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var rows = parse(json);
            StorePage(endpoint, rows);
            return (rows, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Leaderboard {Endpoint} failed", endpoint);
            return ([], "network error");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Leaderboard {Endpoint} timed out", endpoint);
            return ([], "timed out");
        }
    }

    /// <summary>
    /// The public API fabricates a scaffold profile (default name derived from the queried id, all
    /// stats zero) for unknown ids. Detect it so a typo'd id never masquerades as real stats.
    /// </summary>
    public static bool LooksLikePlaceholderProfile(CopsPlayerProfile profile)
        => profile.Name.StartsWith("OPS-", StringComparison.Ordinal)
            && string.Equals(profile.Name, "OPS-" + profile.UserId.ToString("D9", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && profile.Seasons.All(s => s.Ranked.IsZero && s.Casual.IsZero && s.Custom.IsZero);

    /// <summary>Raw fetch that parses the answer as a profile array; empty on any failure.</summary>
    private async Task<(IReadOnlyList<CopsPlayerProfile> Profiles, CopsLookupStatus Failure, string Problem)> FetchRawAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ([], CopsLookupStatus.NotFound, "HTTP " + (int)response.StatusCode);
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var profile = CopsProfileParser.Parse(json);
            if (profile is null)
            {
                return ([], CopsLookupStatus.NotFound, "the answer was not a profile");
            }
            return ([profile], default(CopsLookupStatus), string.Empty);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Batch profile lookup failed");
            return ([], CopsLookupStatus.Error, "network error");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Batch profile lookup timed out");
            return ([], CopsLookupStatus.Error, "timed out");
        }
    }

    private async Task<(CopsPlayerProfile? Profile, CopsLookupStatus Failure, string Problem)> FetchAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
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

    private IReadOnlyList<T>? TryGetCachedPage<T>(string endpoint)
    {
        lock (_leaderboardCacheGate)
        {
            if (_leaderboardCache.TryGetValue(endpoint, out var entry)
                && entry.Rows is IReadOnlyList<T> rows
                && DateTimeOffset.UtcNow - entry.At < LeaderboardCacheLifetime)
            {
                return rows;
            }
        }
        return null;
    }

    private void StorePage<T>(string endpoint, IReadOnlyList<T> rows)
    {
        lock (_leaderboardCacheGate)
        {
            _leaderboardCache[endpoint] = (DateTimeOffset.UtcNow, rows);
        }
    }

    // Disposing the client is safe with the shared handler: it was created with disposeHandler: false.
    public void Dispose() => _http.Dispose();
}
