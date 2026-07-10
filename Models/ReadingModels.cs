namespace AiPulse.Models;

/// <summary>An article the user saved to read later.</summary>
public sealed class BookmarkItem
{
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string SourceName { get; init; } = "";
    public string Category { get; init; } = "";
    public string ContentType { get; init; } = "News";
    public string Level { get; init; } = "Intermediate";
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>A rule that hides matching items from the News feed - the inverse of the watchlist.</summary>
public sealed class ExcludeFilter
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Pattern { get; set; }
    /// <summary>False = plain substring match; true = Pattern is a regular expression.</summary>
    public bool IsRegex { get; set; }
}

/// <summary>Persisted, per-user reading state (bookmarks, read items, last visit, prefs) - see ReadingStateService.</summary>
public sealed class ReadingState
{
    public List<BookmarkItem> Bookmarks { get; set; } = new();
    public HashSet<string> ReadLinks { get; set; } = new();
    public HashSet<string> CompletedModules { get; set; } = new();
    public DateTimeOffset LastNewsVisit { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Keywords to highlight and to fire notifications for.</summary>
    public List<string> Watchlist { get; set; } = new();

    /// <summary>Rules that hide matching items from the News feed entirely.</summary>
    public List<ExcludeFilter> ExcludeFilters { get; set; } = new();

    /// <summary>Where "Export to Obsidian" writes. Empty = App_Data/exports.</summary>
    public string? ObsidianExportPath { get; set; }
}

/// <summary>A notification alert raised when a watched/important item appears in the feed.</summary>
public sealed class Alert
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string SourceName { get; init; } = "";
    public required string Kind { get; init; } // "Release" or "Watchlist"
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool Read { get; set; }
}
