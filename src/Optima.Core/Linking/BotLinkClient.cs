using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Optima.Core.Net;

namespace Optima.Core.Linking;

/// <summary>Outcome of one call against the bot, so the UI can say what happened rather than fail silently.</summary>
public sealed record BotLinkCallResult<T>(bool Ok, T? Value, string Message)
{
    public static BotLinkCallResult<T> Fail(string message) => new(false, default, message);

    public static BotLinkCallResult<T> Success(T value, string message) => new(true, value, message);
}

/// <summary>
/// The desktop app's side of the OptimaBot link API.
///
/// Linking is deliberately not OAuth: the user runs <c>/link</c> in Discord, the bot mints a
/// short-lived single-use code and DMs it back, and the app here redeems that code with the Critical
/// Ops account id it already knows. The bot is the only party that ever sees both identities.
/// </summary>
public sealed class BotLinkClient : IDisposable
{
    /// <summary>
    /// Where Optima's community bot answers. Settings is prefilled with it, so linking works without
    /// anyone typing an address; a self-hosted bot's address replaces it there.
    /// </summary>
    public const string DefaultBaseUrl = "https://optimabot-production.up.railway.app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<BotLinkClient> _logger;

    public BotLinkClient(ILogger<BotLinkClient> logger)
        : this(logger, HttpPool.Shared, disposeHandler: false)
    {
    }

    // The handler overload exists for tests; production shares the app-wide connection pool.
    public BotLinkClient(ILogger<BotLinkClient> logger, HttpMessageHandler handler)
        : this(logger, handler, disposeHandler: true)
    {
    }

    private BotLinkClient(ILogger<BotLinkClient> logger, HttpMessageHandler handler, bool disposeHandler)
    {
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" +
            (typeof(BotLinkClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
    }

    /// <summary>
    /// Turns what the user typed into the bot's address into a base URL, or null when it cannot be
    /// one. A bare host or "host:port" is assumed to be plain http, which is what a self-hosted bot
    /// on the same machine or LAN answers on.
    /// </summary>
    public static string? NormalizeBaseUrl(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var value = typed.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0
            ? value
            : null;
    }

    /// <summary>
    /// Redeems a link code with this machine's player identity. The webhook, when there is one, is the
    /// channel the bot posts that account's matches and rank changes to afterwards.
    /// </summary>
    public async Task<BotLinkCallResult<BotLinkClaimResponse>> ClaimAsync(
        string baseUrl, string code, long accountId, string inGameName, string? webhookUrl = null,
        CancellationToken ct = default)
        => await PostAsync<BotLinkClaimRequest, BotLinkClaimResponse>(
            baseUrl,
            "v1/link/claim",
            new BotLinkClaimRequest(
                code,
                accountId,
                inGameName,
                typeof(BotLinkClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                webhookUrl),
            ct).ConfigureAwait(false);

    /// <summary>
    /// Asks the bot to send a sample tracker report to this webhook. The bot draws and delivers it, so
    /// the test exercises the same path a real match post takes instead of proving only that the URL
    /// exists.
    /// </summary>
    public async Task<BotLinkCallResult<BotTrackerTestResponse>> TestTrackerAsync(
        string baseUrl, string webhookUrl, CancellationToken ct = default)
        => await PostAsync<BotTrackerTestRequest, BotTrackerTestResponse>(
            baseUrl,
            "v1/tracker/test",
            new BotTrackerTestRequest(webhookUrl),
            ct).ConfigureAwait(false);

    /// <summary>Asks the bot who, if anyone, this Critical Ops account is linked to.</summary>
    public async Task<BotLinkCallResult<BotLinkStatusResponse>> GetStatusAsync(
        string baseUrl, long accountId, CancellationToken ct = default)
    {
        var url = BuildUrl(baseUrl, "v1/link/status?accountId=" + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (url is null)
        {
            return BotLinkCallResult<BotLinkStatusResponse>.Fail("The OptimaBot address in config.json does not look like a URL.");
        }

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return await ReadAsync<BotLinkStatusResponse>(response, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Bot link status lookup failed");
            return BotLinkCallResult<BotLinkStatusResponse>.Fail(Unreachable(baseUrl));
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return BotLinkCallResult<BotLinkStatusResponse>.Fail("OptimaBot did not answer in time. Try again in a moment.");
        }
    }

    private async Task<BotLinkCallResult<TResponse>> PostAsync<TRequest, TResponse>(
        string baseUrl, string relative, TRequest body, CancellationToken ct)
    {
        var url = BuildUrl(baseUrl, relative);
        if (url is null)
        {
            return BotLinkCallResult<TResponse>.Fail("The OptimaBot address in config.json does not look like a URL.");
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
            return await ReadAsync<TResponse>(response, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Bot link call failed");
            return BotLinkCallResult<TResponse>.Fail(Unreachable(baseUrl));
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return BotLinkCallResult<TResponse>.Fail("OptimaBot did not answer in time. Try again in a moment.");
        }
    }

    private async Task<BotLinkCallResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        // The bot answers 4xx with the same JSON body as a success, so the message it wrote for the
        // user is what the UI shows; only a body that is not a payload at all falls back to the code.
        T? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // A reverse proxy, a captive portal or a crash page answers HTML, and an empty body is not
            // JSON either. Neither is a reason to throw into the link window, so this falls through to
            // the sentence about the status code.
            _logger.LogDebug(ex, "OptimaBot answered with something that is not JSON");
            payload = default;
        }

        if (payload is not null)
        {
            return BotLinkCallResult<T>.Success(payload, string.Empty);
        }

        return BotLinkCallResult<T>.Fail(response.StatusCode == HttpStatusCode.NotFound
            ? "OptimaBot did not recognise the request. It may be updating; try again in a minute."
            : $"OptimaBot answered HTTP {(int)response.StatusCode} with no detail.");
    }

    private static string? BuildUrl(string baseUrl, string relative)
        => NormalizeBaseUrl(baseUrl) is { } root ? root + "/" + relative : null;

    private static string Unreachable(string baseUrl)
        => IsOnThisMachine(baseUrl)
            ? $"Nothing answered at {baseUrl}. Is OptimaBot running, and is that address right? "
              + $"If you use the community bot instead, its address is {DefaultBaseUrl}."
            : $"Nothing answered at {baseUrl}. Is OptimaBot running, and is that address right?";

    /// <summary>
    /// Whether the address points at this machine. A claim that cannot reach a loopback address is
    /// either a self-hoster whose bot is not running or somebody whose copy of the app still carries
    /// the old local default, and only the second wants the community bot's address named.
    /// </summary>
    private static bool IsOnThisMachine(string baseUrl)
        => Uri.TryCreate(NormalizeBaseUrl(baseUrl) ?? baseUrl, UriKind.Absolute, out var uri)
           && uri.IsLoopback;

    // Disposing the client is safe with the shared handler: it was created with disposeHandler: false.
    public void Dispose() => _http.Dispose();
}
