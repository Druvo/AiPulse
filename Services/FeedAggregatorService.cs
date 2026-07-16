using System.Collections.Concurrent;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using AiPulse.Models;
using HtmlAgilityPack;

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
    private readonly ContentExtractorService _extractor;
    private readonly WebSubService _webSub;
    private readonly ILogger<FeedAggregatorService> _log;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private FeedResult? _cache;

    public FeedAggregatorService(IHttpClientFactory httpFactory, KnowledgeBaseService kb, SourceHealthService health, ContentExtractorService extractor, WebSubService webSub, ILogger<FeedAggregatorService> log)
    {
        _httpFactory = httpFactory;
        _kb = kb;
        _health = health;
        _extractor = extractor;
        _webSub = webSub;
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

    /// <summary>How many of a source's newest items get a full-text fetch each poll - bounded so a big feed doesn't hammer its own site.</summary>
    private const int FullTextFetchLimit = 5;

    private async Task<List<FeedItem>> FetchOneAsync(FeedSource source, CancellationToken ct)
    {
        List<FeedItem> items;
        string? hubUrl = null;

        if (source.IsScrape)
        {
            items = await ScrapeAsync(source, ct);
        }
        else
        {
            var client = _httpFactory.CreateClient("feeds");
            using var resp = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var xml = await resp.Content.ReadAsStringAsync(ct);

            // Primary: strict RSS/Atom parsing. Fallback: lenient XDocument parse for feeds
            // that don't fit the strict schema (some blogs emit mixed content the strict reader rejects).
            try
            {
                (items, hubUrl) = ParseWithSyndication(xml, source);
            }
            catch (Exception)
            {
                (items, hubUrl) = ParseLenient(xml, source);
            }

            if (hubUrl is not null)
                await MaybeSubscribeAsync(source, hubUrl, ct);
        }

        var vocab = GetTagVocab();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i] with { Tags = TagFor(items[i], vocab) };

        if (source.FullTextFetch && items.Count > 0)
            items = await ApplyFullTextAsync(items, ct);

        return items;
    }

    /// <summary>
    /// If the feed declares a WebSub hub and we're not already subscribed, ask the hub to push future
    /// updates to us instead of waiting for the next poll. No-op unless WebSub:PublicBaseUrl is configured.
    /// </summary>
    private async Task MaybeSubscribeAsync(FeedSource source, string hubUrl, CancellationToken ct)
    {
        if (!_webSub.Enabled || source.Id == 0) return;
        try
        {
            if (!await _webSub.HasActiveSubscriptionAsync(source.Id))
                await _webSub.SubscribeAsync(source.Id, source.Url, hubUrl, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebSub subscribe check failed for {Source}", source.Name);
        }
    }

    /// <summary>
    /// Merges items pushed by a WebSub hub into the live cache immediately, instead of waiting for the
    /// next scheduled poll. Called from the /websub/callback POST endpoint.
    /// </summary>
    public async Task<List<FeedItem>> MergePushedContentAsync(FeedSource source, string xml, CancellationToken ct = default)
    {
        List<FeedItem> items;
        try
        {
            (items, _) = ParseWithSyndication(xml, source);
        }
        catch (Exception)
        {
            (items, _) = ParseLenient(xml, source);
        }

        var vocab = GetTagVocab();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i] with { Tags = TagFor(items[i], vocab) };

        if (items.Count == 0)
            return items;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_cache is null)
            {
                // Nothing polled yet this run - a push arriving first is fine, just seed the cache with it.
                _cache = new FeedResult { Items = items, FetchedAt = DateTimeOffset.Now };
            }
            else
            {
                var existingLinks = new HashSet<string>(_cache.Items.Select(i => i.Link), StringComparer.OrdinalIgnoreCase);
                var merged = _cache.Items.Concat(items.Where(i => !existingLinks.Contains(i.Link)));
                _cache = new FeedResult
                {
                    Items = Deduplicate(merged).OrderByDescending(i => i.Published).ToList(),
                    Errors = _cache.Errors,
                    FetchedAt = _cache.FetchedAt
                };
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        return items;
    }

    /// <summary>Fetches the full article for the newest few items and attaches it as FullText, so summary-only feeds become readable in-app.</summary>
    private async Task<List<FeedItem>> ApplyFullTextAsync(List<FeedItem> items, CancellationToken ct)
    {
        var targets = items.OrderByDescending(i => i.Published).Take(FullTextFetchLimit).ToList();
        var fullTexts = new Dictionary<string, string?>();

        using var gate = new SemaphoreSlim(3);
        var tasks = targets.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                fullTexts[item.Link] = await _extractor.FetchFullTextAsync(item.Link, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        for (var i = 0; i < items.Count; i++)
        {
            if (fullTexts.TryGetValue(items[i].Link, out var text) && text is not null)
                items[i] = items[i] with { FullText = text };
        }
        return items;
    }

    /// <summary>Scrapes an HTML page with the source's admin-configured XPath selectors, for sites with no RSS/Atom feed.</summary>
    private static async Task<List<FeedItem>> ScrapeAsync(FeedSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.ScrapeItemXPath))
            throw new InvalidOperationException("Scrape source is missing an item XPath selector.");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AiPulse/1.0 (+https://localhost; personal AI news dashboard)");
        var html = await http.GetStringAsync(source.Url, ct);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var itemNodes = doc.DocumentNode.SelectNodes(source.ScrapeItemXPath);
        if (itemNodes is null)
            return new List<FeedItem>();

        var pageUri = new Uri(source.Url);
        var result = new List<FeedItem>();

        foreach (var node in itemNodes.Take(30))
        {
            var linkNode = string.IsNullOrWhiteSpace(source.ScrapeLinkXPath)
                ? node.SelectSingleNode(".//a")
                : node.SelectSingleNode(source.ScrapeLinkXPath);
            var href = linkNode?.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var absoluteLink = Uri.TryCreate(pageUri, href, out var resolved) ? resolved.ToString() : href;

            var titleNode = string.IsNullOrWhiteSpace(source.ScrapeTitleXPath)
                ? linkNode
                : node.SelectSingleNode(source.ScrapeTitleXPath);
            var rawTitle = titleNode?.InnerText ?? linkNode?.InnerText;
            var itemText = CleanText(System.Net.WebUtility.HtmlDecode(node.InnerText), 320);
            var title = DeriveTitle(rawTitle is null ? null : System.Net.WebUtility.HtmlDecode(rawTitle), itemText, source.Name);

            var published = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(source.ScrapeDateXPath))
            {
                var dateNode = node.SelectSingleNode(source.ScrapeDateXPath);
                var dateAttr = dateNode?.GetAttributeValue("datetime", "");
                var dateText = string.IsNullOrWhiteSpace(dateAttr) ? dateNode?.InnerText : dateAttr;
                if (!string.IsNullOrWhiteSpace(dateText) && DateTimeOffset.TryParse(dateText.Trim(), out var parsed))
                    published = parsed;
            }

            result.Add(new FeedItem
            {
                Title = title,
                Link = CleanUrl(absoluteLink),
                Summary = "",
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

    private static (List<FeedItem> Items, string? HubUrl) ParseWithSyndication(string xml, FeedSource source)
    {
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var feed = SyndicationFeed.Load(reader);
        if (feed is null)
            return (new List<FeedItem>(), null);

        var hubUrl = feed.Links.FirstOrDefault(l => l.RelationshipType == "hub")?.Uri?.ToString();

        var result = new List<FeedItem>();
        foreach (var item in feed.Items.Take(20))
        {
            var link = item.Links.FirstOrDefault(l => l.RelationshipType != "hub")?.Uri?.ToString() ?? "";
            var published = item.PublishDate != default ? item.PublishDate
                : item.LastUpdatedTime != default ? item.LastUpdatedTime
                : DateTimeOffset.Now;

            var rawSummary = ExtractSummary(item);
            var summary = CleanText(rawSummary, 320);
            var imageUrl = ResolveImageUrl(
                ExtractMediaImage(item.ElementExtensions)
                    ?? item.Links.FirstOrDefault(l => l.RelationshipType == "enclosure" && (l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))?.Uri?.ToString()
                    ?? ExtractImgTag(rawSummary),
                source.Url);

            result.Add(new FeedItem
            {
                Title = DeriveTitle(item.Title?.Text, summary, source.Name),
                Link = CleanUrl(link),
                Summary = summary,
                Published = published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags,
                ImageUrl = imageUrl
            });
        }
        return (result, hubUrl);
    }

    private static readonly System.Xml.Linq.XNamespace MediaNs = "http://search.yahoo.com/mrss/";

    /// <summary>Looks for a media:thumbnail or media:content(medium=image) anywhere inside the item's raw extension XML (covers media:group-wrapped thumbnails, e.g. YouTube's feed format).</summary>
    private static string? ExtractMediaImage(SyndicationElementExtensionCollection extensions)
    {
        foreach (var ext in extensions)
        {
            System.Xml.Linq.XElement el;
            try { el = ext.GetObject<System.Xml.Linq.XElement>(); }
            catch { continue; }

            var thumb = el.DescendantsAndSelf().FirstOrDefault(e => e.Name == MediaNs + "thumbnail");
            var thumbUrl = thumb?.Attribute("url")?.Value;
            if (!string.IsNullOrWhiteSpace(thumbUrl)) return thumbUrl;

            var content = el.DescendantsAndSelf().FirstOrDefault(e => e.Name == MediaNs + "content"
                && ((string?)e.Attribute("medium") == "image" || ((string?)e.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true));
            var contentUrl = content?.Attribute("url")?.Value;
            if (!string.IsNullOrWhiteSpace(contentUrl)) return contentUrl;
        }
        return null;
    }

    /// <summary>Same media:thumbnail/media:content lookup as <see cref="ExtractMediaImage(SyndicationElementExtensionCollection)"/>, for the lenient XDocument parse path.</summary>
    private static string? ExtractMediaImage(System.Xml.Linq.XElement entry)
    {
        var thumbUrl = entry.Descendants(MediaNs + "thumbnail").FirstOrDefault()?.Attribute("url")?.Value;
        if (!string.IsNullOrWhiteSpace(thumbUrl)) return thumbUrl;

        var content = entry.Descendants(MediaNs + "content").FirstOrDefault(e =>
            (string?)e.Attribute("medium") == "image" || ((string?)e.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
        return content?.Attribute("url")?.Value;
    }

    private static readonly Regex ImgTagRegex = new(@"<img[^>]+src=[""']([^""'>]+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Fallback when the feed has no media/enclosure image: grabs the first &lt;img&gt; embedded in the item's raw (unstripped) summary/content HTML.</summary>
    private static string? ExtractImgTag(string rawHtml)
    {
        if (string.IsNullOrEmpty(rawHtml)) return null;
        var m = ImgTagRegex.Match(rawHtml);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Resolves a possibly-relative image URL against the feed's own URL, same approach ScrapeAsync uses for links.</summary>
    private static string? ResolveImageUrl(string? raw, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, raw, out var resolved))
            return resolved.ToString();
        return Uri.IsWellFormedUriString(raw, UriKind.Absolute) ? raw : null;
    }

    /// <summary>Tolerant parser used when the strict reader rejects a feed. Handles RSS items and Atom entries.</summary>
    private static (List<FeedItem> Items, string? HubUrl) ParseLenient(string xml, FeedSource source)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml, System.Xml.Linq.LoadOptions.None);
        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        var result = new List<FeedItem>();

        // Hub link lives at the feed/channel level (RSS <atom:link rel="hub"> or Atom <link rel="hub">), not per-item.
        var hubUrl = doc.Descendants().Where(e => e.Name.LocalName == "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") == "hub")
            ?.Attribute("href")?.Value;

        // RSS: //item ; Atom: //entry
        var entries = doc.Descendants("item").Concat(doc.Descendants(atom + "entry")).Take(20);
        foreach (var e in entries)
        {
            var title = e.Element("title")?.Value ?? e.Element(atom + "title")?.Value;
            var link = e.Element("link")?.Value;
            if (string.IsNullOrWhiteSpace(link))
                link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") != "self")?.Attribute("href")?.Value;
            var summaryRaw = e.Element("description")?.Value
                ?? e.Element(atom + "summary")?.Value
                ?? e.Element(atom + "content")?.Value ?? "";
            var summary = CleanText(summaryRaw, 320);
            var dateStr = e.Element("pubDate")?.Value
                ?? e.Element(atom + "updated")?.Value
                ?? e.Element(atom + "published")?.Value;
            DateTimeOffset.TryParse(dateStr, out var published);

            var enclosure = e.Element("enclosure");
            var enclosureUrl = (string?)enclosure?.Attribute("type") is { } encType && encType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? (string?)enclosure.Attribute("url")
                : null;
            var imageUrl = ResolveImageUrl(ExtractMediaImage(e) ?? enclosureUrl ?? ExtractImgTag(summaryRaw), source.Url);

            result.Add(new FeedItem
            {
                Title = DeriveTitle(title, summary, source.Name),
                Link = CleanUrl(link ?? ""),
                Summary = summary,
                Published = published == default ? DateTimeOffset.Now : published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags,
                ImageUrl = imageUrl
            });
        }
        return (result, hubUrl);
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

    /// <summary>
    /// Feeds without a per-item title (common for microblog-style posts - Mastodon, some YouTube
    /// Community posts, etc.) used to show as a bare "(untitled)", which read as broken rather than
    /// "this platform just doesn't have titles". Derive something readable instead: the raw title if
    /// there is one, otherwise the start of the summary, otherwise a plain fallback naming the source.
    /// </summary>
    public static string DeriveTitle(string? rawTitle, string cleanedSummary, string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            var cleaned = CleanText(rawTitle, 200);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        if (!string.IsNullOrWhiteSpace(cleanedSummary))
        {
            const int max = 80;
            return cleanedSummary.Length > max ? cleanedSummary[..max].TrimEnd() + "…" : cleanedSummary;
        }

        return $"New post from {sourceName}";
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
