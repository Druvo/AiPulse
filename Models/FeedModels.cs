namespace AiPulse.Models;

/// <summary>A source we pull updates from (RSS/Atom feed or GitHub releases feed).</summary>
public sealed class FeedSource
{
    /// <summary>DB row id (0 for sources not yet persisted). Used to key WebSub subscriptions to a source.</summary>
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Category { get; init; } // News, Research, Tools, Community
    public bool Enabled { get; init; } = true;
    /// <summary>Optional note shown in the Sources page.</summary>
    public string? Note { get; init; }
    /// <summary>Kind of content this source produces: News, Tutorial, Release, Paper, Discussion, Video.</summary>
    public string ContentType { get; init; } = "News";
    /// <summary>How advanced the content typically is: Beginner, Intermediate, Advanced.</summary>
    public string Level { get; init; } = "Intermediate";
    /// <summary>Base topic tags applied to every item from this source (e.g. RAG, agents).</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
    /// <summary>Fetch the full article text (not just the feed's teaser) for the newest items each poll.</summary>
    public bool FullTextFetch { get; init; }
    /// <summary>If true, Url is an HTML page to scrape with the XPath selectors below instead of an RSS/Atom feed.</summary>
    public bool IsScrape { get; init; }
    /// <summary>XPath to each item's container element (e.g. "//article" or "//div[@class='post']").</summary>
    public string? ScrapeItemXPath { get; init; }
    /// <summary>Relative XPath (from the item container) to the title text. Falls back to the link text if unset.</summary>
    public string? ScrapeTitleXPath { get; init; }
    /// <summary>Relative XPath (from the item container) to the link's anchor element.</summary>
    public string? ScrapeLinkXPath { get; init; }
    /// <summary>Relative XPath (from the item container) to a date string, if present. Falls back to "now" if unset/unparsable.</summary>
    public string? ScrapeDateXPath { get; init; }
}

/// <summary>A single normalized item pulled from any feed.</summary>
public sealed record FeedItem
{
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset Published { get; init; }
    public required string SourceName { get; init; }
    public required string Category { get; init; }
    public string ContentType { get; init; } = "News";
    public string Level { get; init; } = "Intermediate";
    public string[] Tags { get; init; } = Array.Empty<string>();
    /// <summary>Other sources that covered the same story, when cross-source dedup merges near-identical titles.</summary>
    public string[] AlsoSeenOn { get; init; } = Array.Empty<string>();
    /// <summary>Full article text, when the source has FullTextFetch enabled and extraction succeeded. Null otherwise.</summary>
    public string? FullText { get; init; }
    /// <summary>Thumbnail/banner image URL, when the feed itself carries one (media:thumbnail, enclosure, or an inline &lt;img&gt; in the summary). Null if none found - no extra fetch is made to find one.</summary>
    public string? ImageUrl { get; init; }
    /// <summary>Byline, when the feed declares one (Atom/RSS &lt;author&gt; or the common non-standard &lt;dc:creator&gt;). Null if the feed doesn't carry one.</summary>
    public string? Author { get; init; }

    /// <summary>Rough reading-time estimate from Summary/FullText word count at ~200wpm - null when there's too little text to bother estimating. Computed, not stored, so bookmarks/etc. get it automatically from whatever text they already carry.</summary>
    public int? ReadingMinutes
    {
        get
        {
            var text = FullText ?? Summary;
            if (string.IsNullOrWhiteSpace(text)) return null;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return words < 40 ? null : Math.Max(1, (int)Math.Round(words / 200.0));
        }
    }
}

/// <summary>Result of one aggregation run, including any sources that failed.</summary>
public sealed class FeedResult
{
    public List<FeedItem> Items { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}
