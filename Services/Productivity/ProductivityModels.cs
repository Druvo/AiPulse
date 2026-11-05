namespace AiPulse.Services.Productivity;

/// <summary>
/// A saved News query a signed-in user can re-run from /saved, and optionally be alerted about. Keywords use the small query syntax
/// <see cref="SavedSearchQuery"/> understands (words, "quoted phrases", -exclusions); tags and sources narrow it further.
/// </summary>
public sealed class SavedSearchRule
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Free text: plain words must all appear, "quoted phrases" must appear verbatim, -word must not appear.</summary>
    public string Keywords { get; set; } = "";
    /// <summary>Comma separated. An item matches when it carries any of them (case-insensitive).</summary>
    public string Tags { get; set; } = "";
    /// <summary>Newline separated source names (names may contain commas). An item matches when it came from any of them.</summary>
    public string Sources { get; set; } = "";
    /// <summary>Raise in-app notifications for new matches.</summary>
    public bool AlertEnabled { get; set; }
    /// <summary>Also post new matches to the user's own webhook (Settings), when they have one.</summary>
    public bool WebhookEnabled { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>When alerts were last switched on. Matches that already existed at that moment are recorded as seen, never alerted.</summary>
    public DateTime? AlertsSinceUtc { get; set; }
    public DateTime? LastAlertUtc { get; set; }
}

/// <summary>
/// One (rule, item) pair the alert evaluator has already handled. The unique index on (RuleId, LinkHash) is the dedupe: an item can
/// alert a rule at most once, however many polls it stays in the feed. <see cref="Notified"/> is false for baseline rows (items that
/// already matched when alerts were switched on) and for matches beyond the per-poll cap.
/// </summary>
public sealed class SavedSearchHit
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    public int UserId { get; set; }
    public string LinkHash { get; set; } = "";
    public string Link { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceName { get; set; } = "";
    public bool Notified { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A named group of the user's bookmarks. Bookmarks themselves stay in the reading-state file; this only groups them by link.</summary>
public sealed class BookmarkCollection
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Membership of a bookmark (by link) in a collection. A bookmark can sit in several collections ("copy").</summary>
public sealed class BookmarkCollectionEntry
{
    public long Id { get; set; }
    public int CollectionId { get; set; }
    public int UserId { get; set; }
    public string Link { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

/// <summary>The user's own tags and private note on one bookmark. Never shown to anyone else.</summary>
public sealed class BookmarkAnnotation
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Link { get; set; } = "";
    /// <summary>Comma separated, normalised by <see cref="BookmarkCollectionLogic.NormaliseTags"/>.</summary>
    public string Tags { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Per-user switches for the weekly email digest and the dashboard's first-run checklist.</summary>
public sealed class UserProductivitySettings
{
    /// <summary>Same value as AppUser.Id (one row per user).</summary>
    public int UserId { get; set; }
    public bool DigestOptIn { get; set; }
    public DateTime? DigestLastSentUtc { get; set; }
    /// <summary>Random value baked into unsubscribe links. Rotating it (on every opt-in) invalidates every older link.</summary>
    public string DigestNonce { get; set; } = "";
    public bool OnboardingDismissed { get; set; }
    /// <summary>Comma separated checklist steps the user ticked by hand (the ones the app cannot detect, like installing the app).</summary>
    public string OnboardingManualSteps { get; set; } = "";
}
