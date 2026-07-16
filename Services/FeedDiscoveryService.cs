using HtmlAgilityPack;

namespace AiPulse.Services;

/// <summary>
/// Given a plain website URL (not necessarily a feed itself), tries to find its actual RSS/Atom feed -
/// so adding a source can start from "paste the site's homepage" instead of requiring the user to already
/// know/find the feed URL. Plain HTTP heuristics, no AI: check if the URL is already a feed, else look for
/// a declared &lt;link rel="alternate"&gt; in its &lt;head&gt;, else try a few conventional feed paths.
/// </summary>
public sealed class FeedDiscoveryService
{
    private static readonly string[] CommonFeedPaths =
        { "/feed", "/feed/", "/rss.xml", "/rss", "/atom.xml", "/index.xml", "/feed.xml" };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<FeedDiscoveryService> _log;

    public FeedDiscoveryService(IHttpClientFactory httpFactory, ILogger<FeedDiscoveryService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Returns the discovered feed URL, or null if nothing looked like a feed.</summary>
    public async Task<string?> DiscoverFeedUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var baseUri))
            return null;

        var client = _httpFactory.CreateClient("feeds");
        string html;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            html = await client.GetStringAsync(baseUri, cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Feed discovery fetch failed for {Url}", url);
            return null;
        }

        // Already a feed - the URL the user pasted was the real thing all along.
        var trimmed = html.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Contains("<rss", StringComparison.OrdinalIgnoreCase) && trimmed.IndexOf("<rss", StringComparison.OrdinalIgnoreCase) < 500) ||
            (trimmed.Contains("<feed", StringComparison.OrdinalIgnoreCase) && trimmed.IndexOf("<feed", StringComparison.OrdinalIgnoreCase) < 500))
            return baseUri.ToString();

        // Look for a declared alternate feed link in the page's <head> - the standard way sites advertise this.
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var linkNode = doc.DocumentNode.SelectSingleNode(
            "//link[@rel='alternate' and (contains(@type,'rss') or contains(@type,'atom'))]");
        var href = linkNode?.GetAttributeValue("href", "");
        if (!string.IsNullOrWhiteSpace(href) && Uri.TryCreate(baseUri, href, out var declaredUri))
            return declaredUri.ToString();

        // Fall back to a few conventional feed paths - works for a lot of blogging platforms that don't
        // bother declaring <link rel="alternate">.
        foreach (var path in CommonFeedPaths)
        {
            var candidate = new Uri(baseUri, path);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var resp = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return candidate.ToString();
            }
            catch { /* try the next path */ }
        }

        return null;
    }
}
