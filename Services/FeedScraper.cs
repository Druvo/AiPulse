using AiPulse.Models;
using HtmlAgilityPack;

namespace AiPulse.Services;

/// <summary>Scrapes an HTML page with a source's admin-configured XPath selectors, for sites with no RSS/Atom feed.</summary>
internal static class FeedScraper
{
    public static async Task<List<FeedItem>> ScrapeAsync(FeedSource source, CancellationToken ct)
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
            var itemText = FeedTextUtils.CleanText(System.Net.WebUtility.HtmlDecode(node.InnerText), 320);
            var title = FeedTextUtils.DeriveTitle(rawTitle is null ? null : System.Net.WebUtility.HtmlDecode(rawTitle), itemText, source.Name);

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
                Link = FeedTextUtils.CleanUrl(absoluteLink),
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
}
