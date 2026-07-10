namespace AiPulse.Models;

/// <summary>A source we pull updates from (RSS/Atom feed or GitHub releases feed).</summary>
public sealed class FeedSource
{
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
}

/// <summary>Result of one aggregation run, including any sources that failed.</summary>
public sealed class FeedResult
{
    public List<FeedItem> Items { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}
