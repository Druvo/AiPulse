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
    private readonly SourceHealthService _health;
    private readonly ILogger<FeedAggregatorService> _log;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private FeedResult? _cache;

    public FeedAggregatorService(IHttpClientFactory httpFactory, KnowledgeBaseService kb, SourceHealthService health, ILogger<FeedAggregatorService> log)
    {
        _httpFactory = httpFactory;
        _kb = kb;
        _health = health;
        _log = log;
    }

    private Dictionary<string, string>? _tagVocab; // matched word/phrase (lowercase) -> canonical tag
    private readonly ConcurrentDictionary<string, int> _failureStreaks = new();

    public DateTimeOffset? LastFetched => _cache?.FetchedAt;

    /// <summary>Consecutive failed fetches for a source since its last success (0 = healthy or unknown).</summary>
    public int GetFailureStreak(string sourceName) => _failureStreaks.GetValueOrDefault(sourceName);

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
                _failureStreaks[source.Name] = 0;
                _health.RecordResult(source.Name, true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Feed failed: {Source}", source.Name);
                errors.Add($"{source.Name}: {ex.Message}");
                _failureStreaks.AddOrUpdate(source.Name, 1, (_, n) => n + 1);
                _health.RecordResult(source.Name, false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new FeedResult
        {
            Items = Deduplicate(items).OrderByDescending(i => i.Published).ToList(),
            Errors = errors.OrderBy(e => e).ToList(),
            FetchedAt = DateTimeOffset.Now
        };
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "your", "you", "are", "was", "were",
        "into", "how", "why", "what", "new", "now", "will", "can", "has", "have", "its", "our"
    };

    /// <summary>
    /// Merges items that are almost certainly the same story reported by multiple sources - matched by a
    /// normalized-title key (significant words, sorted) within a 3-day window, so a big release covered by
    /// five blogs shows up once instead of five times. Pure string matching, no AI involved.
    /// </summary>
    private static IEnumerable<FeedItem> Deduplicate(IEnumerable<FeedItem> items)
    {
        var groups = new Dictionary<string, List<FeedItem>>();
        foreach (var item in items)
        {
            var key = NormalizedTitleKey(item.Title);
            if (key is null)
            {
                yield return item; // title too short/generic to safely dedupe - keep as-is
                continue;
            }
            if (!groups.TryGetValue(key, out var bucket))
                groups[key] = bucket = new List<FeedItem>();
            bucket.Add(item);
        }

        foreach (var bucket in groups.Values)
        {
            if (bucket.Count == 1)
            {
                yield return bucket[0];
                continue;
            }

            // Split into sub-clusters by publish time (within 3 days of each other) - avoids merging
            // an old and a new story that happen to share generic significant words.
            foreach (var cluster in ClusterByTime(bucket))
            {
                if (cluster.Count == 1)
                {
                    yield return cluster[0];
                    continue;
                }

                var primary = cluster.OrderBy(i => i.Published).First();
                var others = cluster.Where(i => i != primary).Select(i => i.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var mergedTags = cluster.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                yield return primary with { AlsoSeenOn = others, Tags = mergedTags };
            }
        }
    }

    private static List<List<FeedItem>> ClusterByTime(List<FeedItem> items)
    {
        var sorted = items.OrderBy(i => i.Published).ToList();
        var clusters = new List<List<FeedItem>>();
        foreach (var item in sorted)
        {
            var cluster = clusters.LastOrDefault();
            if (cluster is not null && item.Published - cluster[^1].Published <= TimeSpan.FromDays(3))
                cluster.Add(item);
            else
                clusters.Add(new List<FeedItem> { item });
        }
        return clusters;
    }

    /// <summary>Lowercase, strip punctuation, drop stopwords/short words, sort what's left. Null if too little signal.</summary>
    private static string? NormalizedTitleKey(string title)
    {
        var words = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\s]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !Stopwords.Contains(w))
            .Distinct()
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        return words.Count < 3 ? null : string.Join(' ', words);
    }

    private async Task<List<FeedItem>> FetchOneAsync(FeedSource source, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("feeds");
        using var resp = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct);

        // Primary: strict RSS/Atom parsing. Fallback: lenient XDocument parse for feeds
        // that don't fit the strict schema (some blogs emit mixed content the strict reader rejects).
        List<FeedItem> items;
        try
        {
            items = ParseWithSyndication(xml, source);
        }
        catch (Exception)
        {
            items = ParseLenient(xml, source);
        }

        var vocab = GetTagVocab();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i] with { Tags = TagFor(items[i], vocab) };

        return items;
    }

    /// <summary>Builds a lowercase term/alias -> canonical tag lookup from the curated glossary, once.</summary>
    private Dictionary<string, string> GetTagVocab()
    {
        if (_tagVocab is not null)
            return _tagVocab;

        var vocab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in _kb.Glossary)
        {
            vocab.TryAdd(term.Term, term.Term);
            foreach (var alias in term.Aliases)
                vocab.TryAdd(alias, term.Term);
        }
        return _tagVocab = vocab;
    }

    /// <summary>Union of the source's base tags plus any glossary terms/aliases matched in the item's title+summary.</summary>
    private static string[] TagFor(FeedItem item, Dictionary<string, string> vocab)
    {
        var text = item.Title + " " + item.Summary;
        var tags = new HashSet<string>(item.Tags, StringComparer.OrdinalIgnoreCase);

        foreach (var (needle, canonical) in vocab)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(needle)}\b", RegexOptions.IgnoreCase))
                tags.Add(canonical);
        }

        return tags.ToArray();
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
                Link = CleanUrl(link),
                Summary = CleanText(ExtractSummary(item), 320),
                Published = published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags
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
                Link = CleanUrl(link ?? ""),
                Summary = CleanText(summary, 320),
                Published = published == default ? DateTimeOffset.Now : published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags
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

    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name", "utm_reader",
        "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid",
        "ref", "ref_src", "ref_url", "_hsenc", "_hsmi", "spm", "yclid", "vero_id", "oly_enc_id", "oly_anon_id"
    };

    /// <summary>Strips known tracking query params from an item's link before it's stored/displayed/exported - privacy hygiene, not functional.</summary>
    private static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return url;

        var baseUrl = url[..qIndex];
        var query = url[(qIndex + 1)..];
        var fragment = "";
        var hashIndex = query.IndexOf('#');
        if (hashIndex >= 0)
        {
            fragment = query[hashIndex..];
            query = query[..hashIndex];
        }

        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !TrackingParams.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0])))
            .ToList();

        return kept.Count == 0 ? baseUrl + fragment : $"{baseUrl}?{string.Join('&', kept)}{fragment}";
    }
}
