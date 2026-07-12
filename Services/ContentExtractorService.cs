using System.Collections.Concurrent;
using System.Text;
using HtmlAgilityPack;

namespace AiPulse.Services;

/// <summary>
/// Fetches an article's full HTML and extracts the main readable content, for feeds that only publish a
/// teaser summary - so you can read the whole thing in AiPulse instead of clicking out. A simple
/// readability heuristic (prefer &lt;article&gt;/&lt;main&gt;, else the densest text block), not AI.
/// Results are cached by URL for the life of the process so repeated polls don't re-fetch the same page.
/// </summary>
public sealed class ContentExtractorService
{
    private const int MaxCacheEntries = 500;
    private const int MaxChars = 6000;

    private static readonly string[] StripTags =
        { "script", "style", "nav", "header", "footer", "aside", "form", "noscript", "iframe", "svg", "button" };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ContentExtractorService> _log;
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public ContentExtractorService(IHttpClientFactory httpFactory, ILogger<ContentExtractorService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Returns the extracted full text, or null if the fetch/extraction failed. Cached per URL.</summary>
    public async Task<string?> FetchFullTextAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (_cache.TryGetValue(url, out var cached))
            return cached;

        string? extracted = null;
        try
        {
            var client = _httpFactory.CreateClient("feeds");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var html = await client.GetStringAsync(url, cts.Token);
            extracted = Extract(html);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Full-text fetch failed for {Url}", url);
        }

        Remember(url, extracted);
        return extracted;
    }

    private void Remember(string url, string? value)
    {
        _cache[url] = value;
        _cacheOrder.Enqueue(url);
        while (_cacheOrder.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    private static string? Extract(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var tag in StripTags)
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes is null) continue;
            foreach (var node in nodes.ToList())
                node.Remove();
        }

        // Prefer semantic containers if present.
        var candidate = doc.DocumentNode.SelectSingleNode("//article")
            ?? doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//*[@role='main']");

        // Otherwise pick whichever top-level block has the most text - a cheap density heuristic.
        candidate ??= doc.DocumentNode.SelectNodes("//div|//section")
            ?.OrderByDescending(n => n.InnerText.Length)
            .FirstOrDefault();

        var text = (candidate ?? doc.DocumentNode).InnerText;
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n+", "\n\n");
        text = text.Trim();

        if (text.Length < 200)
            return null; // too little extracted to be useful - likely a paywall/JS-rendered page

        return text.Length > MaxChars ? text[..MaxChars].TrimEnd() + "…" : text;
    }
}
