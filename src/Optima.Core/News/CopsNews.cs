using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Optima.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Optima.Core.News;

/// <summary>One update entry from the official Critical Ops updates page.</summary>
public sealed record CopsNewsEntry(
    string Name,
    string Version,
    string Status,
    IReadOnlyList<string> Headlines,
    string NotesUrl)
{
    public bool IsLive => string.Equals(Status, "LIVE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parser for criticalopsgame.com/updates/. The page is plain server-rendered HTML with a
/// stable per-entry shape (verified 2026-08-30): an article holding a status tag, an
/// "NAME - X.Y.Z" heading and headline paragraphs. There is no feed or API on the site, so
/// this is scraping by explicit decision (Q4/Q5) and it fails SOFT: shape drift yields an
/// empty list, and the UI says the feed is unavailable rather than guessing.
/// </summary>
public static partial class CopsNewsParser
{
    private const string BaseUrl = "https://criticalopsgame.com";

    [GeneratedRegex(
        """<article>\s*<p class="comment[^"]*">(?<status>[^<]+)</p>\s*<h3><a href="(?<url>[^"]+)">(?<title>[^<]+)</a></h3>\s*<div>(?<body>.*?)</div>\s*</article>""",
        RegexOptions.Singleline)]
    private static partial Regex ArticlePattern();

    [GeneratedRegex(@"<p>(?<text>.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex(@"href=""(?<href>/news/[^""]+)""")]
    private static partial Regex NotesLinkPattern();

    [GeneratedRegex(@"(?<version>\d+(?:\.\d+)+)\s*$")]
    private static partial Regex TrailingVersionPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    public static IReadOnlyList<CopsNewsEntry> Parse(string html)
    {
        var entries = new List<CopsNewsEntry>();
        try
        {
            foreach (Match article in ArticlePattern().Matches(html))
            {
                var title = WebUtility.HtmlDecode(article.Groups["title"].Value).Trim();
                var versionMatch = TrailingVersionPattern().Match(title);
                var version = versionMatch.Success ? versionMatch.Groups["version"].Value : "";
                var name = versionMatch.Success
                    ? title[..versionMatch.Index].TrimEnd(' ', '-').Trim()
                    : title;

                var body = article.Groups["body"].Value;
                var headlines = new List<string>();
                foreach (Match paragraph in ParagraphPattern().Matches(body))
                {
                    var text = WebUtility.HtmlDecode(TagPattern().Replace(paragraph.Groups["text"].Value, "")).Trim();
                    // The trailing "Full ... patch notes!" paragraph is the link, not a headline.
                    if (text.Length > 0 && !text.Contains("patch notes", StringComparison.OrdinalIgnoreCase))
                    {
                        headlines.Add(text);
                    }
                }

                var notesLink = NotesLinkPattern().Match(body);
                var notesUrl = notesLink.Success
                    ? BaseUrl + notesLink.Groups["href"].Value
                    : BaseUrl + article.Groups["url"].Value;

                entries.Add(new CopsNewsEntry(
                    name,
                    version,
                    WebUtility.HtmlDecode(article.Groups["status"].Value).Trim().ToUpperInvariant(),
                    headlines,
                    notesUrl));
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return entries;
    }

    /// <summary>Newest LIVE version on the page, or null when none parses.</summary>
    public static string? LatestLiveVersion(IReadOnlyList<CopsNewsEntry> entries)
        => entries.FirstOrDefault(e => e.IsLive && e.Version.Length > 0)?.Version;
}

/// <summary>
/// Fetch + cache for the official updates page. One request per refresh, cached to disk so
/// the page renders offline; the site is the only endpoint this service ever contacts.
/// </summary>
public sealed class CopsNewsService : IDisposable
{
    private const string UpdatesUrl = "https://criticalopsgame.com/updates/";
    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<CopsNewsService> _logger;

    public CopsNewsService(AppPaths paths, ILogger<CopsNewsService> logger)
    {
        _paths = paths;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Optima/" + (typeof(CopsNewsService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
    }

    private string CachePath => Path.Combine(_paths.Root, "news-cache.json");

    /// <summary>Fetches and caches the feed; falls back to the disk cache on any failure.</summary>
    public async Task<IReadOnlyList<CopsNewsEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(UpdatesUrl, ct).ConfigureAwait(false);
            var entries = CopsNewsParser.Parse(html);
            if (entries.Count > 0)
            {
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(entries), ct).ConfigureAwait(false);
                return entries;
            }
            _logger.LogWarning("The updates page parsed to zero entries; its shape may have changed");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug(ex, "News fetch failed; trying the cache");
        }
        return await LoadCacheAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CopsNewsEntry>> LoadCacheAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return [];
            }
            var json = await File.ReadAllTextAsync(CachePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<CopsNewsEntry>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    public void Dispose() => _http.Dispose();
}
