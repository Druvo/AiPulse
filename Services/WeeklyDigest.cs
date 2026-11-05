using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>A story several sources covered. <see cref="Coverage"/> counts distinct sources, the item's own included.</summary>
public sealed record DigestStory(FeedItem Item, int Coverage, IReadOnlyList<string> Sources);

/// <summary>The newest release from one source in the window, and how many releases that source shipped in it.</summary>
public sealed record DigestRelease(string SourceName, FeedItem Latest, int Count);

public sealed record WeeklyDigestModel(
    DateTimeOffset From,
    DateTimeOffset To,
    int StoryCount,
    int SourceCount,
    IReadOnlyList<TopicCount> TopTopics,
    IReadOnlyList<DigestStory> MostCovered,
    IReadOnlyList<DigestRelease> NewReleases)
{
    public bool IsEmpty => StoryCount == 0 && NewReleases.Count == 0;
}

/// <summary>
/// "This week in AI": pure functions that turn the saved news history into a weekly digest (the public /this-week page, its Atom feed
/// and the weekly email). No clock, database or server: callers pass the items and the window, so ranking and ties are testable.
/// </summary>
public static class WeeklyDigest
{
    public const int DefaultTopics = 10;
    public const int DefaultStories = 10;
    public const int DefaultReleases = 12;

    /// <summary>The rolling seven days ending at <paramref name="now"/>.</summary>
    public static WeeklyDigestModel LastSevenDays(IEnumerable<FeedItem> items, DateTimeOffset now) => Build(items, now.AddDays(-7), now);

    /// <summary>
    /// Builds the digest for [from, to]. Topics rank by story count (ties alphabetical, case-insensitive); most-covered stories need at
    /// least two distinct sources and rank by source count, then newest, then title, then link (so equal stories always come out in the
    /// same order); releases keep the newest per source, newest first, ties by source name.
    /// </summary>
    public static WeeklyDigestModel Build(IEnumerable<FeedItem> items, DateTimeOffset from, DateTimeOffset to,
        int topics = DefaultTopics, int stories = DefaultStories, int releases = DefaultReleases)
    {
        var window = items
            .Where(i => !string.IsNullOrEmpty(i.Link) && i.Published >= from && i.Published <= to)
            .GroupBy(i => i.Link, StringComparer.Ordinal).Select(g => g.First())
            .ToList();
        var headlines = window.Where(PublicFeeds.IsHeadline).ToList();

        var topTopics = PublicFeeds.Topics(headlines, from, take: topics, minCount: 2);

        var covered = headlines
            .Select(i => new DigestStory(i, 0, SourcesOf(i)))
            .Select(s => s with { Coverage = s.Sources.Count })
            .Where(s => s.Coverage >= 2)
            .OrderByDescending(s => s.Coverage)
            .ThenByDescending(s => s.Item.Published)
            .ThenBy(s => s.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Item.Link, StringComparer.Ordinal)
            .Take(stories)
            .ToList();

        var newReleases = window
            .Where(i => i.ContentType == "Release")
            .GroupBy(i => i.SourceName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(i => i.Published).ThenBy(i => i.Link, StringComparer.Ordinal).First();
                return new DigestRelease(latest.SourceName, latest, g.Count());
            })
            .OrderByDescending(r => r.Latest.Published)
            .ThenBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .Take(releases)
            .ToList();

        var sourceCount = headlines.SelectMany(SourcesOf).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new WeeklyDigestModel(from, to, headlines.Count, sourceCount, topTopics, covered, newReleases);
    }

    /// <summary>The item's own source plus every other source it was also seen on, distinct and case-insensitive, own source first.</summary>
    public static IReadOnlyList<string> SourcesOf(FeedItem item) =>
        new[] { item.SourceName }.Concat(item.AlsoSeenOn)
            .Select(s => (s ?? "").Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Monday 00:00 UTC of the ISO week containing <paramref name="moment"/>.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        var daysSinceMonday = ((int)utc.DayOfWeek + 6) % 7;
        return new DateTimeOffset(utc.Date.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }

    /// <summary>e.g. "2026-W45" - the ISO week-numbering year and week, stable as a feed entry id.</summary>
    public static string WeekLabel(DateTimeOffset weekStart)
    {
        var d = weekStart.UtcDateTime;
        return $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";
    }

    /// <summary>
    /// Atom feed with one entry per ISO week: the week so far (its entry updates in place as the week fills up) and the
    /// <paramref name="weeks"/> - 1 weeks before it. Weeks with nothing in them are left out.
    /// </summary>
    public static string Atom(IEnumerable<FeedItem> items, string origin, DateTimeOffset now, int weeks = 4)
    {
        var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
        var list = items.ToList();
        var thisWeek = WeekStart(now);
        var entries = new List<XElement>();
        DateTimeOffset? newest = null;

        for (var w = 0; w < Math.Max(1, weeks); w++)
        {
            var start = thisWeek.AddDays(-7 * w);
            var end = w == 0 ? now : start.AddDays(7).AddTicks(-1);
            var digest = Build(list, start, end);
            if (digest.IsEmpty) continue;

            var updated = w == 0 ? now : start.AddDays(7);
            if (updated > now) updated = now;
            newest = newest is null || updated > newest ? updated : newest;
            var label = WeekLabel(start);
            entries.Add(new XElement(atom + "entry",
                new XElement(atom + "title", (w == 0 ? "This week in AI so far" : "This week in AI") + $" ({RangeText(start, end)})"),
                new XElement(atom + "link", new XAttribute("href", $"{origin}/this-week")),
                new XElement(atom + "id", $"urn:aipulse:this-week:{label}"),
                new XElement(atom + "updated", Iso(updated)),
                new XElement(atom + "summary", Summary(digest)),
                new XElement(atom + "content", new XAttribute("type", "html"), ToHtmlFragment(digest, origin))));
        }

        var feed = new XElement(atom + "feed",
            new XElement(atom + "title", "AiPulse - This week in AI"),
            new XElement(atom + "subtitle", "A weekly round-up of the most-covered AI stories, busiest topics and new releases."),
            new XElement(atom + "id", $"{origin}/this-week.xml"),
            new XElement(atom + "updated", Iso(newest ?? now)),
            new XElement(atom + "link", new XAttribute("rel", "self"), new XAttribute("href", $"{origin}/this-week.xml")),
            new XElement(atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", $"{origin}/this-week")),
            new XElement(atom + "generator", "AiPulse"));
        feed.Add(entries);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), feed).Declaration + "\n" + feed.ToString(SaveOptions.DisableFormatting);
    }

    public static string RangeText(DateTimeOffset from, DateTimeOffset to)
    {
        var (f, t, c) = (from.UtcDateTime, to.UtcDateTime, CultureInfo.InvariantCulture);
        return f.Month == t.Month && f.Year == t.Year
            ? $"{f.ToString("MMM d", c)}–{t.ToString("d, yyyy", c)}"
            : $"{f.ToString("MMM d", c)} – {t.ToString("MMM d, yyyy", c)}";
    }

    /// <summary>One line: "42 stories from 17 sources; most covered: ...".</summary>
    public static string Summary(WeeklyDigestModel d)
    {
        if (d.IsEmpty) return "A quiet week: nothing new was collected.";
        var sb = new StringBuilder($"{d.StoryCount} {Plural(d.StoryCount, "story", "stories")} from {d.SourceCount} {Plural(d.SourceCount, "source", "sources")}");
        if (d.MostCovered.Count > 0) sb.Append($"; most covered: {d.MostCovered[0].Item.Title}");
        if (d.NewReleases.Count > 0) sb.Append($"; {d.NewReleases.Count} {Plural(d.NewReleases.Count, "project", "projects")} shipped releases");
        return sb.Append('.').ToString();
    }

    /// <summary>Plain-text rendering (the email's text part). Links are absolute.</summary>
    public static string ToPlainText(WeeklyDigestModel d, string origin, string? unsubscribeUrl = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"This week in AI ({RangeText(d.From, d.To)})");
        sb.AppendLine(Summary(d));
        sb.AppendLine();
        if (d.MostCovered.Count > 0)
        {
            sb.AppendLine("MOST-COVERED STORIES");
            foreach (var s in d.MostCovered)
            {
                sb.AppendLine($"- {s.Item.Title} ({s.Coverage} sources)");
                sb.AppendLine($"  {s.Item.Link}");
            }
            sb.AppendLine();
        }
        if (d.TopTopics.Count > 0)
        {
            sb.AppendLine("TOP TOPICS");
            sb.AppendLine(string.Join(", ", d.TopTopics.Select(t => $"{t.Tag} ({t.Count})")));
            sb.AppendLine();
        }
        if (d.NewReleases.Count > 0)
        {
            sb.AppendLine("NEW RELEASES");
            foreach (var r in d.NewReleases)
                sb.AppendLine($"- {r.SourceName}: {r.Latest.Title}{(r.Count > 1 ? $" (+{r.Count - 1} more)" : "")}  {r.Latest.Link}");
            sb.AppendLine();
        }
        sb.AppendLine($"Read it online: {origin}/this-week");
        if (unsubscribeUrl is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"You get this because you switched on the weekly digest in AiPulse. Unsubscribe: {unsubscribeUrl}");
        }
        return sb.ToString();
    }

    /// <summary>The HTML body shared by the feed entries and the email: simple, inline-styled markup every client renders. All text is encoded.</summary>
    public static string ToHtmlFragment(WeeklyDigestModel d, string origin)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        static string Href(string url) => WebUtility.HtmlEncode(SafeHref(url));
        var sb = new StringBuilder();
        sb.Append($"<p>{E(Summary(d))}</p>");
        if (d.MostCovered.Count > 0)
        {
            sb.Append("<h2>Most-covered stories</h2><ol>");
            foreach (var s in d.MostCovered)
                sb.Append($"<li><a href=\"{Href(s.Item.Link)}\">{E(s.Item.Title)}</a> <small>({s.Coverage} sources: {E(string.Join(", ", s.Sources))})</small></li>");
            sb.Append("</ol>");
        }
        if (d.TopTopics.Count > 0)
        {
            sb.Append("<h2>Top topics</h2><p>");
            sb.Append(string.Join(" · ", d.TopTopics.Select(t => $"<a href=\"{Href(origin + PublicFeeds.TagPath(t.Tag))}\">{E(t.Tag)}</a> ({t.Count})")));
            sb.Append("</p>");
        }
        if (d.NewReleases.Count > 0)
        {
            sb.Append("<h2>New releases</h2><ul>");
            foreach (var r in d.NewReleases)
                sb.Append($"<li><strong>{E(r.SourceName)}</strong>: <a href=\"{Href(r.Latest.Link)}\">{E(r.Latest.Title)}</a>{(r.Count > 1 ? $" <small>(+{r.Count - 1} more)</small>" : "")}</li>");
            sb.Append("</ul>");
        }
        return sb.ToString();
    }

    /// <summary>A full HTML email document around <see cref="ToHtmlFragment"/>.</summary>
    public static string ToHtmlEmail(WeeklyDigestModel d, string origin, string unsubscribeUrl)
    {
        static string e(string s) => WebUtility.HtmlEncode(s);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>This week in AI</title></head>"
            + "<body style=\"font-family:system-ui,-apple-system,Segoe UI,sans-serif;line-height:1.5;color:#1d1b29;max-width:640px;margin:0 auto;padding:16px\">"
            + $"<h1 style=\"font-size:22px\">This week in AI <small style=\"color:#555\">({e(RangeText(d.From, d.To))})</small></h1>"
            + ToHtmlFragment(d, origin)
            + $"<p><a href=\"{e(origin)}/this-week\">Read it on AiPulse</a></p>"
            + $"<hr><p style=\"font-size:12px;color:#555\">You get this because you switched on the weekly digest in AiPulse. <a href=\"{e(unsubscribeUrl)}\">Unsubscribe</a>.</p>"
            + "</body></html>";
    }

    /// <summary>Only http(s) links are rendered as links; anything else (javascript:, data:) becomes "#".</summary>
    public static string SafeHref(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? url : "#";

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string Plural(int n, string one, string many) => n == 1 ? one : many;
}
