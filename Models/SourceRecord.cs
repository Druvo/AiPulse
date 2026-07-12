namespace AiPulse.Models;

/// <summary>
/// EF Core-mapped persisted source row. Kept separate from <see cref="FeedSource"/> (used throughout
/// FeedAggregatorService/News/etc.) so nothing downstream needs to change shape - KnowledgeBaseService
/// maps between the two.
/// </summary>
public sealed class SourceRecord
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string Category { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Note { get; set; }
    public string ContentType { get; set; } = "News";
    public string Level { get; set; } = "Intermediate";
    /// <summary>Comma-separated tags (existing tag values never contain commas).</summary>
    public string TagsCsv { get; set; } = "";

    public bool FullTextFetch { get; set; }
    public bool IsScrape { get; set; }
    public string? ScrapeItemXPath { get; set; }
    public string? ScrapeTitleXPath { get; set; }
    public string? ScrapeLinkXPath { get; set; }
    public string? ScrapeDateXPath { get; set; }

    public string[] Tags
    {
        get => string.IsNullOrWhiteSpace(TagsCsv)
            ? Array.Empty<string>()
            : TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => TagsCsv = string.Join(',', value);
    }

    public FeedSource ToFeedSource() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Category = Category,
        Enabled = Enabled,
        Note = Note,
        ContentType = ContentType,
        Level = Level,
        Tags = Tags,
        FullTextFetch = FullTextFetch,
        IsScrape = IsScrape,
        ScrapeItemXPath = ScrapeItemXPath,
        ScrapeTitleXPath = ScrapeTitleXPath,
        ScrapeLinkXPath = ScrapeLinkXPath,
        ScrapeDateXPath = ScrapeDateXPath
    };
}
