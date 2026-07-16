using System.Collections.Concurrent;
using HtmlAgilityPack;

namespace AiPulse.Services;

/// <summary>
/// Fetches and caches each source's favicon (and declared brand/theme color) so the News Feed can show a
/// small per-source icon and accent without ever calling out to a third-party favicon CDN (which would
/// otherwise reveal your whole source list to that third party on every page load). Tries /favicon.ico
/// first, then falls back to parsing the site's homepage for a <![CDATA[<link rel="icon">]]> tag. Cached
/// in memory by host for the life of the process, same eviction pattern as <see cref="ContentExtractorService"/>.
/// </summary>
public sealed class FaviconService
{
    private const int MaxCacheEntries = 500;

    private static readonly string[] IconLinkRels = { "icon", "shortcut icon", "apple-touch-icon" };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<FaviconService> _log;
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)?> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    private readonly ConcurrentDictionary<string, string?> _themeColorCache = new();
    private readonly ConcurrentDictionary<string, byte> _themeColorFetching = new();

    public FaviconService(IHttpClientFactory httpFactory, ILogger<FaviconService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Returns the cached favicon for a host, fetching (and caching, including "known missing") on first request. Null if no favicon could be found.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetFaviconAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        if (_cache.TryGetValue(host, out var cached))
            return cached;

        var result = await FetchAsync(host, ct);
        Remember(host, result);
        return result;
    }

    /// <summary>Synchronous cache check for a host's declared brand color - never blocks a render. False if nothing's cached yet (not even "known missing").</summary>
    public bool TryGetCachedThemeColor(string host, out string? color) => _themeColorCache.TryGetValue(host, out color);

    /// <summary>Kicks off a background fetch of this host's declared &lt;meta name="theme-color"&gt; if one isn't already cached or in flight. Fire-and-forget by design - callers fall back to a generated color until a later render picks up the cached result, same "no history yet" pattern as source uptime%.</summary>
    public void EnsureThemeColorFetchStarted(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || _themeColorCache.ContainsKey(host) || !_themeColorFetching.TryAdd(host, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                _themeColorCache[host] = await FetchThemeColorAsync(host);
            }
            finally
            {
                _themeColorFetching.TryRemove(host, out _);
            }
        });
    }

    private async Task<string?> FetchThemeColorAsync(string host)
    {
        try
        {
            var client = _httpFactory.CreateClient("feeds");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var html = await client.GetStringAsync($"https://{host}/", cts.Token);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var content = doc.DocumentNode.SelectSingleNode("//meta[@name='theme-color']")?.GetAttributeValue("content", "");
            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Theme-color fetch failed for {Host}", host);
            return null;
        }
    }

    private async Task<(byte[] Bytes, string ContentType)?> FetchAsync(string host, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("feeds");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var direct = await TryFetchImageAsync(client, $"https://{host}/favicon.ico", cts.Token);
        if (direct is not null)
            return direct;

        try
        {
            var html = await client.GetStringAsync($"https://{host}/", cts.Token);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var links = doc.DocumentNode.SelectNodes("//link[@rel]");
            var iconHref = links?
                .Where(n => IconLinkRels.Contains(n.GetAttributeValue("rel", "").Trim(), StringComparer.OrdinalIgnoreCase))
                .Select(n => n.GetAttributeValue("href", ""))
                .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));

            if (!string.IsNullOrWhiteSpace(iconHref) && Uri.TryCreate(new Uri($"https://{host}/"), iconHref, out var resolved))
                return await TryFetchImageAsync(client, resolved.ToString(), cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Favicon homepage lookup failed for {Host}", host);
        }

        return null;
    }

    private async Task<(byte[] Bytes, string ContentType)?> TryFetchImageAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/x-icon";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : (bytes, contentType);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Favicon fetch failed for {Url}", url);
            return null;
        }
    }

    private void Remember(string host, (byte[] Bytes, string ContentType)? value)
    {
        _cache[host] = value;
        _cacheOrder.Enqueue(host);
        while (_cacheOrder.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }
}
