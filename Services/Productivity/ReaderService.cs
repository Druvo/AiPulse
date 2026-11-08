using System.Collections.Concurrent;
using AiPulse.Models;
using HtmlAgilityPack;

namespace AiPulse.Services.Productivity;

/// <summary>What the reading view shows. <see cref="BodyHtml"/> is always sanitised (see <see cref="ReaderSanitizer"/>).</summary>
public sealed record ReaderArticle(
    FeedItem Item,
    string BodyHtml,
    int Words,
    int Minutes,
    ReaderBodySource Source);

public enum ReaderBodySource { Summary, FeedFullText, FetchedPage }

/// <summary>
/// Builds the reading view for an item. Only links already in the saved news history are accepted, so /read can never be used to make
/// the server fetch an arbitrary URL; the original page is fetched only on an explicit request, for a known item, and cached.
/// </summary>
public sealed class ReaderService
{
    private const int MaxCached = 200;
    private const int MaxPageBytes = 3_000_000;
    private readonly FeedHistoryService _history;
    private readonly FeedAggregatorService _feeds;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ReaderService> _log;
    private readonly ConcurrentDictionary<string, string> _fetched = new();
    private readonly ConcurrentQueue<string> _order = new();

    public ReaderService(FeedHistoryService history, FeedAggregatorService feeds, IHttpClientFactory http, ILogger<ReaderService> log)
    {
        _history = history;
        _feeds = feeds;
        _http = http;
        _log = log;
    }

    /// <summary>The known item with this link (live cache first, then history), or null.</summary>
    public FeedItem? Find(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        return _feeds.PeekCache()?.Items.FirstOrDefault(i => i.Link == link)
            ?? _history.Items.FirstOrDefault(i => i.Link == link);
    }

    /// <summary>The best body available without a network call: a previously fetched page, the feed's full text, or the summary.</summary>
    public ReaderArticle? Get(string? link)
    {
        var item = Find(link);
        if (item is null) return null;
        if (_fetched.TryGetValue(item.Link, out var cached)) return Build(item, cached, ReaderBodySource.FetchedPage);
        return BuildFromFeed(item);
    }

    public static ReaderArticle BuildFromFeed(FeedItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FullText) && item.FullText.Length > (item.Summary?.Length ?? 0))
            return Build(item, ReaderSanitizer.ParagraphsFromText(item.FullText), ReaderBodySource.FeedFullText);
        var summary = item.Summary ?? "";
        var html = summary.Contains('<') ? ReaderSanitizer.Sanitize(summary, item.Link) : ReaderSanitizer.ParagraphsFromText(summary);
        return Build(item, html, ReaderBodySource.Summary);
    }

    private static ReaderArticle Build(FeedItem item, string html, ReaderBodySource source)
    {
        var words = ReaderSanitizer.WordCount(html);
        return new ReaderArticle(item, html, words, ReaderSanitizer.ReadingMinutes(words), source);
    }

    /// <summary>Fetches the original page for a known item and extracts its main content. Null when the item is unknown or extraction fails.</summary>
    public async Task<ReaderArticle?> FetchFullAsync(string? link, CancellationToken ct = default)
    {
        var item = Find(link);
        if (item is null) return null;
        if (_fetched.TryGetValue(item.Link, out var cached)) return Build(item, cached, ReaderBodySource.FetchedPage);
        if (!Uri.TryCreate(item.Link, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        if (IsLocalTarget(uri)) return null;

        try
        {
            var client = _http.CreateClient("feeds");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxPageBytes) break;
            }
            var html = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var body = ExtractMainHtml(html, item.Link);
            if (ReaderSanitizer.WordCount(body) < 80) return null; // a paywall or a script-rendered page: keep the summary instead
            Remember(item.Link, body);
            return Build(item, body, ReaderBodySource.FetchedPage);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Reader fetch failed for {Url}", item.Link);
            return null;
        }
    }

    /// <summary>Picks the article's main element (article, main, role=main, else the densest block) and sanitises it.</summary>
    public static string ExtractMainHtml(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var node = doc.DocumentNode.SelectNodes("//article")?.OrderByDescending(n => n.InnerText.Length).FirstOrDefault()
            ?? doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//*[@role='main']")
            ?? doc.DocumentNode.SelectNodes("//div|//section")?.OrderByDescending(n => n.SelectNodes(".//p")?.Count ?? 0).FirstOrDefault()
            ?? doc.DocumentNode.SelectSingleNode("//body")
            ?? doc.DocumentNode;
        return ReaderSanitizer.Sanitize(node.InnerHtml, baseUrl);
    }

    /// <summary>Defence in depth: a feed item pointing at localhost or a private-network IP literal is never fetched by the server.</summary>
    public static bool IsLocalTarget(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        var b = ip.MapToIPv4().GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6) return false;
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    private void Remember(string link, string html)
    {
        _fetched[link] = html;
        _order.Enqueue(link);
        while (_order.Count > MaxCached && _order.TryDequeue(out var oldest))
            _fetched.TryRemove(oldest, out _);
    }
}
