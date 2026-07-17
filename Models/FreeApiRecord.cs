namespace AiPulse.Models;

/// <summary>
/// EF Core-mapped persisted Free-API row - DB-backed (like <see cref="SourceRecord"/>) instead of a static
/// JSON file, so admin approvals from the discovery queue and staleness flags persist without a redeploy.
/// Data/free-apis.json only seeds the table once, on first run - after that the DB is the source of truth,
/// same pattern KnowledgeBaseService already uses for Sources.
/// </summary>
public sealed class FreeApiRecord
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string FreeTierSummary { get; set; }
    public required string Models { get; set; }
    public string RateLimit { get; set; } = "";
    public required string SignupUrl { get; set; }
    public string HowToUse { get; set; } = "";
    public bool OpenAiCompatible { get; set; }
    public string? DocsUrl { get; set; }
    public required string LastVerified { get; set; }

    /// <summary>Set by FreeApiDiscoveryService's link-check pass when SignupUrl/DocsUrl stopped resolving - cleared automatically once they resolve again.</summary>
    public bool NeedsReview { get; set; }
    public string? NeedsReviewReason { get; set; }

    public FreeApiEntry ToEntry() => new()
    {
        Id = Id,
        Name = Name,
        Category = Category,
        FreeTierSummary = FreeTierSummary,
        Models = Models,
        RateLimit = RateLimit,
        SignupUrl = SignupUrl,
        HowToUse = HowToUse,
        OpenAiCompatible = OpenAiCompatible,
        DocsUrl = DocsUrl,
        LastVerified = LastVerified,
        NeedsReview = NeedsReview,
        NeedsReviewReason = NeedsReviewReason
    };
}

/// <summary>
/// A provider name found by FreeApiDiscoveryService in a community-curated free-API list that isn't already
/// on AiPulse's own list, awaiting admin review. Never auto-published - approving takes the admin to the
/// normal add-entry form pre-filled with just the name and discovery link, since the source list's fields
/// rarely map cleanly onto AiPulse's structured shape (rate limits, agent-wiring notes, etc.).
/// </summary>
public sealed class FreeApiCandidate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string SourceUrl { get; set; }
    public string? RawNote { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.Now;
    public string Status { get; set; } = "Pending"; // Pending, Approved, Dismissed
    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>One completed FreeApiDiscoveryService run, kept so the admin can see discovery activity over time on the Free AI APIs page instead of only ever seeing the latest run's summary.</summary>
public sealed class DiscoveryRunLogEntry
{
    public int Id { get; set; }
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.Now;
    public int CandidatesFound { get; set; }
    public int EntriesFlagged { get; set; }
    public required string Summary { get; set; }
}
