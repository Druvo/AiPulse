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
}

/// <summary>A single normalized item pulled from any feed.</summary>
public sealed class FeedItem
{
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset Published { get; init; }
    public required string SourceName { get; init; }
    public required string Category { get; init; }
}

/// <summary>Result of one aggregation run, including any sources that failed.</summary>
public sealed class FeedResult
{
    public List<FeedItem> Items { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}
