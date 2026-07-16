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
    /// <summary>Snapshot of the original item's summary/thumbnail at save time, so the Reading List can render the same card as the News Feed. Null for bookmarks saved before this field existed.</summary>
    public string? Summary { get; init; }
    public string? ImageUrl { get; init; }
}

/// <summary>A rule that hides matching items from the News feed - the inverse of the watchlist.</summary>
public sealed class ExcludeFilter
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Pattern { get; set; }
    /// <summary>False = plain substring match; true = Pattern is a regular expression.</summary>
    public bool IsRegex { get; set; }
}

/// <summary>
/// A named, reusable News Feed filter combination ("smart folder") - e.g. "Agent news this week" = a
/// keyword plus a relative date window. The date window is stored as a day-count (not fixed dates) so
/// re-applying it later always means "the last N days from now," not a frozen historical range.
/// </summary>
public sealed class SavedSearch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Search { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Level { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public string Sort { get; set; } = "newest";
    /// <summary>Null = no date filter; otherwise "items from the last N days" re-evaluated at apply time.</summary>
    public int? DateWindowDays { get; set; }
}

/// <summary>
/// One "marked as read" event, appended (never rewritten) so the Reading Stats page can show real
/// per-day/per-source history - the older `ReadLinks` set only tracks current membership, not when.
/// </summary>
public sealed class ReadEvent
{
    public required string Link { get; init; }
    public string SourceName { get; init; } = "";
    public int ReadingMinutes { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// An additional outgoing webhook scoped to specific keywords - lets different topics route to different
/// channels (e.g. "MCP" alerts to one Slack channel, everything else to another) instead of every alert
/// going to the single global <see cref="ReadingState.WebhookUrl"/>.
/// </summary>
public sealed class WebhookRoute
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; set; }
    /// <summary>Matched against the alert's title/details/source, case-insensitive substring. Empty = catch-all (fires for every alert).</summary>
    public List<string> Keywords { get; set; } = new();
}

/// <summary>A simple embed on the Dashboard - either an iframe (external page) or raw HTML/text.</summary>
public sealed class DashboardWidget
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; set; }
    /// <summary>"Iframe" (Content is a URL) or "Html" (Content is rendered as sanitized-ish raw markup).</summary>
    public string Type { get; set; } = "Iframe";
    public required string Content { get; set; }
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

    /// <summary>True once the user has dismissed the Dashboard's first-visit welcome banner.</summary>
    public bool WelcomeDismissed { get; set; }

    /// <summary>Slack/Discord/generic incoming-webhook URL for release + watchlist alerts. Null = disabled.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Custom Dashboard embeds (iframes or raw HTML snippets), user-managed.</summary>
    public List<DashboardWidget> DashboardWidgets { get; set; } = new();

    /// <summary>Named, reusable News Feed filter combinations ("smart folders").</summary>
    public List<SavedSearch> SavedSearches { get; set; } = new();

    /// <summary>Append-only "marked as read" history, for the Reading Stats page.</summary>
    public List<ReadEvent> ReadHistory { get; set; } = new();

    /// <summary>Additional keyword-scoped webhooks, alongside the single catch-all <see cref="WebhookUrl"/>.</summary>
    public List<WebhookRoute> WebhookRoutes { get; set; } = new();

    /// <summary>
    /// Sources this user has hidden from their own News Feed, by exact name (always matches
    /// <see cref="FeedItem.SourceName"/> verbatim, both ultimately from the same <c>SourceRecord.Name</c> -
    /// no case-insensitive comparer needed, unlike free-typed input). Sources themselves stay
    /// global/admin-managed (one shared fetch/cache for everyone); this only filters at display time, so
    /// different users can have meaningfully different feeds without each needing their own source list.
    /// </summary>
    public HashSet<string> MutedSources { get; set; } = new();

    /// <summary>
    /// MD5("username:password") for the Fever API (a separate password from the main login, chosen by the
    /// user in Settings) - stored as the digest itself, never the plaintext, since that's all the protocol
    /// needs to compare against on each request.
    /// </summary>
    public string? FeverApiKey { get; set; }
}

/// <summary>A notification alert raised when a watched/important item appears in the feed.</summary>
public sealed class Alert
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string SourceName { get; init; } = "";
    public required string Kind { get; init; } // "Release", "Watchlist", or "Trend"
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool Read { get; set; }
    /// <summary>The underlying item's own title/version text (e.g. the actual release name) - richer than the alert's own Title, which for a grouped Release alert is just the source name.</summary>
    public string? Details { get; init; }
    /// <summary>How many new items this alert represents - &gt;1 for a throttled/grouped Release alert covering several new items from the same source in one poll.</summary>
    public int Count { get; init; } = 1;
}
