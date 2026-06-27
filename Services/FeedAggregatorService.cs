using System.Collections.Concurrent;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Pulls and normalizes items from all enabled RSS/Atom feeds. Pure HTTP + XML parsing - no AI involved.
/// Results are cached briefly so navigating between pages doesn't re-hit the network every time.
/// </summary>
public sealed class FeedAggregatorService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private static readonly Regex HtmlTags = new("<[^>]+>", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly KnowledgeBaseService _kb;
    private readonly ILogger<FeedAggregatorService> _log;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private FeedResult? _cache;

    public FeedAggregatorService(IHttpClientFactory httpFactory, KnowledgeBaseService kb, ILogger<FeedAggregatorService> log)
    {
        _httpFactory = httpFactory;
        _kb = kb;
        _log = log;
    }

    public DateTimeOffset? LastFetched => _cache?.FetchedAt;

    /// <summary>Returns cached results if fresh; otherwise fetches. Pass force=true to bypass the cache.</summary>
    public async Task<FeedResult> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is not null && DateTimeOffset.Now - _cache.FetchedAt < CacheFor)
            return _cache;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have just refreshed while we waited.
            if (!force && _cache is not null && DateTimeOffset.Now - _cache.FetchedAt < CacheFor)
                return _cache;

            _cache = await FetchAllAsync(ct);
            return _cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<FeedResult> FetchAllAsync(CancellationToken ct)
    {
        var sources = _kb.Sources.Where(s => s.Enabled).ToList();
        var items = new ConcurrentBag<FeedItem>();
        var errors = new ConcurrentBag<string>();

        // Limit concurrency so we're polite to feed hosts.
        using var gate = new SemaphoreSlim(5);

        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var item in await FetchOneAsync(source, ct))
                    items.Add(item);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Feed failed: {Source}", source.Name);
                errors.Add($"{source.Name}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new FeedResult
        {
            Items = items.OrderByDescending(i => i.Published).ToList(),
            Errors = errors.OrderBy(e => e).ToList(),
            FetchedAt = DateTimeOffset.Now
        };
    }

    private async Task<List<FeedItem>> FetchOneAsync(FeedSource source, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("feeds");
        using var resp = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct);

        // Primary: strict RSS/Atom parsing. Fallback: lenient XDocument parse for feeds
        // that don't fit the strict schema (some blogs emit mixed content the strict reader rejects).
        try
        {
            return ParseWithSyndication(xml, source);
        }
        catch (Exception)
        {
            return ParseLenient(xml, source);
        }
    }

    private static List<FeedItem> ParseWithSyndication(string xml, FeedSource source)
    {
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var feed = SyndicationFeed.Load(reader);
        if (feed is null)
            return new List<FeedItem>();

        var result = new List<FeedItem>();
        foreach (var item in feed.Items.Take(20))
        {
            var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? "";
            var published = item.PublishDate != default ? item.PublishDate
                : item.LastUpdatedTime != default ? item.LastUpdatedTime
                : DateTimeOffset.Now;

            result.Add(new FeedItem
            {
                Title = CleanText(item.Title?.Text ?? "(untitled)", 200),
                Link = link,
                Summary = CleanText(ExtractSummary(item), 320),
                Published = published,
                SourceName = source.Name,
                Category = source.Category
            });
        }
        return result;
    }

    /// <summary>Tolerant parser used when the strict reader rejects a feed. Handles RSS items and Atom entries.</summary>
    private static List<FeedItem> ParseLenient(string xml, FeedSource source)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml, System.Xml.Linq.LoadOptions.None);
        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        var result = new List<FeedItem>();

        // RSS: //item ; Atom: //entry
        var entries = doc.Descendants("item").Concat(doc.Descendants(atom + "entry")).Take(20);
        foreach (var e in entries)
        {
            var title = e.Element("title")?.Value ?? e.Element(atom + "title")?.Value ?? "(untitled)";
            var link = e.Element("link")?.Value;
            if (string.IsNullOrWhiteSpace(link))
                link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") != "self")?.Attribute("href")?.Value;
            var summary = e.Element("description")?.Value
                ?? e.Element(atom + "summary")?.Value
                ?? e.Element(atom + "content")?.Value ?? "";
            var dateStr = e.Element("pubDate")?.Value
                ?? e.Element(atom + "updated")?.Value
                ?? e.Element(atom + "published")?.Value;
            DateTimeOffset.TryParse(dateStr, out var published);

            result.Add(new FeedItem
            {
                Title = CleanText(title, 200),
                Link = link ?? "",
                Summary = CleanText(summary, 320),
                Published = published == default ? DateTimeOffset.Now : published,
                SourceName = source.Name,
                Category = source.Category
            });
        }
        return result;
    }

    private static string ExtractSummary(SyndicationItem item)
    {
        if (item.Summary?.Text is { Length: > 0 } s)
            return s;
        if (item.Content is TextSyndicationContent tc && tc.Text is { Length: > 0 })
            return tc.Text;
        return "";
    }

    private static string CleanText(string raw, int max)
    {
        var text = HtmlTags.Replace(raw, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > max ? text[..max].TrimEnd() + "…" : text;
    }
}
