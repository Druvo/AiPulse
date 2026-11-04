using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AiPulse.Models;

namespace AiPulse.Services;

public sealed record TopicCount(string Tag, int Count);

/// <summary>
/// What the open (signed-out) pages derive from the saved news history: the Atom feed, the topic index and the release list. Pure
/// functions over a list of items so they are testable without a server, a database or a clock.
/// </summary>
public static class PublicFeeds
{
    public const int FeedLimit = 50;

    /// <summary>Release notes live on /releases and Explore; the news feed and the topic pages are about headlines.</summary>
    public static bool IsHeadline(FeedItem item) => item.ContentType != "Release";

    /// <summary>Tags ranked by how many recent headlines carry them. Case-insensitive; the most common spelling is displayed.</summary>
    public static IReadOnlyList<TopicCount> Topics(IEnumerable<FeedItem> items, DateTimeOffset since, int take = 60, int minCount = 2)
    {
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => IsHeadline(i) && i.Published >= since))
        {
            foreach (var tag in item.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!spellings.TryGetValue(tag, out var forms)) spellings[tag] = forms = new Dictionary<string, int>();
                forms[tag] = forms.GetValueOrDefault(tag) + 1;
            }
        }

        return spellings
            .Select(kv => new TopicCount(kv.Value.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).First().Key, kv.Value.Values.Sum()))
            .Where(t => t.Count >= minCount)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    public static IReadOnlyList<FeedItem> ForTag(IEnumerable<FeedItem> items, string tag, int take = 100) =>
        items.Where(i => IsHeadline(i) && i.Tags.Any(t => string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase)))
             .OrderByDescending(i => i.Published).Take(take).ToList();

    public static IReadOnlyList<FeedItem> Releases(IEnumerable<FeedItem> items, DateTimeOffset now, int take = 120) =>
        items.Where(i => i.ContentType == "Release" && i.Published <= now).OrderByDescending(i => i.Published).Take(take).ToList();

    /// <summary>Atom 1.0. <paramref name="selfPath"/> is the feed's own path plus query, so readers can find it again.</summary>
    public static string Atom(IEnumerable<FeedItem> items, string origin, string title, string selfPath, DateTimeOffset now)
    {
        var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
        var list = items.Take(FeedLimit).ToList();
        var updated = list.Count > 0 ? list.Max(i => i.Published) : now;
        if (updated > now) updated = now;

        var feed = new XElement(atom + "feed",
            new XElement(atom + "title", title),
            new XElement(atom + "subtitle", "Deduplicated AI news from the sources AiPulse follows."),
            new XElement(atom + "id", $"{origin}{selfPath}"),
            new XElement(atom + "updated", updated.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
            new XElement(atom + "link", new XAttribute("rel", "self"), new XAttribute("href", $"{origin}{selfPath}")),
            new XElement(atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", $"{origin}/news")),
            new XElement(atom + "generator", "AiPulse"));

        foreach (var item in list)
        {
            var published = item.Published > now ? now : item.Published;
            var entry = new XElement(atom + "entry",
                new XElement(atom + "title", item.Title),
                new XElement(atom + "link", new XAttribute("href", item.Link)),
                new XElement(atom + "id", EntryId(item.Link)),
                new XElement(atom + "updated", published.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
                new XElement(atom + "category", new XAttribute("term", item.SourceName)));
            if (!string.IsNullOrWhiteSpace(item.Author))
                entry.Add(new XElement(atom + "author", new XElement(atom + "name", item.Author)));
            foreach (var tag in item.Tags) entry.Add(new XElement(atom + "category", new XAttribute("term", tag)));
            var summary = Excerpt(item.Summary);
            if (summary.Length > 0) entry.Add(new XElement(atom + "summary", summary));
            feed.Add(entry);
        }

        // XDocument writes the declaration itself; ToString on the element would omit it. UTF-8 declared, UTF-8 sent.
        return new XDocument(new XDeclaration("1.0", "utf-8", null), feed).Declaration + "\n" + feed.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>A stable id for an entry, so readers do not show it twice when its title is edited later.</summary>
    public static string EntryId(string link) =>
        "urn:sha1:" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant();

    /// <summary>Feed summaries are often HTML; a plain-text teaser keeps readers' rendering and our escaping simple.</summary>
    public static string Excerpt(string html, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " "));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Tags become URL segments; this is the one place that says which are safe and how they are written.</summary>
    public static string TagPath(string tag) => "/topics/" + Uri.EscapeDataString(tag);
}
