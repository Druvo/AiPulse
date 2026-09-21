using System.ServiceModel.Syndication;
using System.Xml;
using System.Text.RegularExpressions;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Turns a feed's raw XML into FeedItems. Two entry points: <see cref="ParseWithSyndication"/> (strict
/// RSS/Atom parsing, tried first) and <see cref="ParseLenient"/> (tolerant XDocument parse for feeds that
/// don't fit the strict schema - some blogs emit mixed content the strict reader rejects).
/// </summary>
internal static class FeedXmlParser
{
    // A feed's own <description>/<content:encoded> is sometimes already the full article (arXiv's abstract,
    // some Medium publications' export) - well past a mere teaser. When it's this long, use it directly as
    // FullText instead of live-fetching the page: skips a redundant request, and some sites (Medium) reject
    // non-browser fetches outright, so this is the only way their content shows up at all.
    private const int FeedContentSubstantialThreshold = 500;
    private const int FullTextFromFeedMaxChars = 6000;

    private static readonly System.Xml.Linq.XNamespace MediaNs = "http://search.yahoo.com/mrss/";
    private static readonly System.Xml.Linq.XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly Regex ImgTagRegex = new(@"<img[^>]+src=[""']([^""'>]+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (List<FeedItem> Items, string? HubUrl) ParseWithSyndication(string xml, FeedSource source)
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
            var summary = FeedTextUtils.CleanText(rawSummary, 320);
            var imageUrl = ResolveImageUrl(
                ExtractMediaImage(item.ElementExtensions)
                    ?? item.Links.FirstOrDefault(l => l.RelationshipType == "enclosure" && (l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))?.Uri?.ToString()
                    ?? ExtractImgTag(rawSummary),
                source.Url);
            var author = ExtractAuthor(item);

            result.Add(new FeedItem
            {
                Title = FeedTextUtils.DeriveTitle(item.Title?.Text, summary, source.Name),
                Link = FeedTextUtils.CleanUrl(link),
                Summary = summary,
                FullText = source.FullTextFetch && rawSummary.Length > FeedContentSubstantialThreshold
                    ? FeedTextUtils.CleanText(rawSummary, FullTextFromFeedMaxChars) : null,
                Published = published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags,
                ImageUrl = imageUrl,
                Author = author
            });
        }
        return (result, hubUrl);
    }

    /// <summary>Tolerant parser used when the strict reader rejects a feed. Handles RSS items and Atom entries.</summary>
    public static (List<FeedItem> Items, string? HubUrl) ParseLenient(string xml, FeedSource source)
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
            var summary = FeedTextUtils.CleanText(summaryRaw, 320);
            var dateStr = e.Element("pubDate")?.Value
                ?? e.Element(atom + "updated")?.Value
                ?? e.Element(atom + "published")?.Value;
            DateTimeOffset.TryParse(dateStr, out var published);

            var enclosure = e.Element("enclosure");
            var enclosureUrl = (string?)enclosure?.Attribute("type") is { } encType && encType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? (string?)enclosure.Attribute("url")
                : null;
            var imageUrl = ResolveImageUrl(ExtractMediaImage(e) ?? enclosureUrl ?? ExtractImgTag(summaryRaw), source.Url);

            var author = ((string?)e.Element(DcNs + "creator"))?.Trim()
                ?? ((string?)e.Element("author"))?.Trim()
                ?? e.Element(atom + "author")?.Element(atom + "name")?.Value?.Trim();

            result.Add(new FeedItem
            {
                Title = FeedTextUtils.DeriveTitle(title, summary, source.Name),
                Link = FeedTextUtils.CleanUrl(link ?? ""),
                Summary = summary,
                FullText = source.FullTextFetch && summaryRaw.Length > FeedContentSubstantialThreshold
                    ? FeedTextUtils.CleanText(summaryRaw, FullTextFromFeedMaxChars) : null,
                Published = published == default ? DateTimeOffset.Now : published,
                SourceName = source.Name,
                Category = source.Category,
                ContentType = source.ContentType,
                Level = source.Level,
                Tags = source.Tags,
                ImageUrl = imageUrl,
                Author = string.IsNullOrWhiteSpace(author) ? null : author
            });
        }
        return (result, hubUrl);
    }

    /// <summary>Byline from Atom/RSS &lt;author&gt; (mapped natively by the syndication API) or the common non-standard &lt;dc:creator&gt; (not natively mapped - read from the item's raw extension XML, same technique as the media-thumbnail lookup).</summary>
    private static string? ExtractAuthor(SyndicationItem item)
    {
        var direct = item.Authors.FirstOrDefault()?.Name;
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

        foreach (var ext in item.ElementExtensions)
        {
            System.Xml.Linq.XElement el;
            try { el = ext.GetObject<System.Xml.Linq.XElement>(); }
            catch { continue; }

            if (el.Name == DcNs + "creator" && !string.IsNullOrWhiteSpace(el.Value))
                return el.Value.Trim();
        }
        return null;
    }

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

    /// <summary>Fallback when the feed has no media/enclosure image: grabs the first &lt;img&gt; embedded in the item's raw (unstripped) summary/content HTML.</summary>
    private static string? ExtractImgTag(string rawHtml)
    {
        if (string.IsNullOrEmpty(rawHtml)) return null;
        var m = ImgTagRegex.Match(rawHtml);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Resolves a possibly-relative image URL against the feed's own URL, same approach the scraper uses for links.</summary>
    private static string? ResolveImageUrl(string? raw, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, raw, out var resolved))
            return resolved.ToString();
        return Uri.IsWellFormedUriString(raw, UriKind.Absolute) ? raw : null;
    }

    private static string ExtractSummary(SyndicationItem item)
    {
        if (item.Summary?.Text is { Length: > 0 } s)
            return s;
        if (item.Content is TextSyndicationContent tc && tc.Text is { Length: > 0 })
            return tc.Text;
        return "";
    }
}
