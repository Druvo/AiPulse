namespace AiPulse.Models;

/// <summary>A glossary entry: a term/concept you keep hearing and what it actually means.</summary>
public sealed class GlossaryTerm
{
    public required string Term { get; init; }
    /// <summary>Other names / spellings people use for the same thing.</summary>
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public required string Category { get; init; } // Concept, Technique, Architecture, Tooling
    public required string ShortDef { get; init; }
    public string Details { get; init; } = "";
    /// <summary>When/why a developer would actually reach for this.</summary>
    public string WhenToUse { get; init; } = "";
    public string[] Related { get; init; } = Array.Empty<string>();
    public string? LearnMoreUrl { get; init; }
}

/// <summary>A tool entry for the "right tool for the right task" matrix.</summary>
public sealed class ToolEntry
{
    public required string Name { get; init; }
    public required string Category { get; init; } // Coding Agent, IDE Assistant, Local Runtime, Framework
    public required string OneLiner { get; init; }
    public string[] BestFor { get; init; } = Array.Empty<string>();
    public string[] NotIdealFor { get; init; } = Array.Empty<string>();
    public string[] TokenTips { get; init; } = Array.Empty<string>();
    public string? Url { get; init; }
}

/// <summary>A reusable best-practice / tip card (token optimization, prompting, workflow).</summary>
public sealed class PracticeTip
{
    public required string Title { get; init; }
    public required string Category { get; init; } // Token Optimization, Prompting, Workflow, Context
    public required string Body { get; init; }
}

/// <summary>
/// A curated entry for the Free AI APIs page - providers with a genuinely free tier (not just a paid
/// trial), for plugging into agent tools (Claude Code, Codex CLI, OpenCode, etc.) that support a custom
/// base URL / OpenAI-compatible endpoint. Hand-maintained, not scraped - provider free-tier terms change
/// too often and aren't published as a structured feed, so this is refreshed manually rather than polled.
/// </summary>
public sealed class FreeApiEntry
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; } // Fully Free, Free Tier, Free Credits
    public required string FreeTierSummary { get; init; }
    public required string Models { get; init; }
    public string RateLimit { get; init; } = "";
    public required string SignupUrl { get; init; }
    /// <summary>Short note on wiring this into an agent CLI - env var/base-URL override, or why it isn't directly agent-compatible.</summary>
    public string HowToUse { get; init; } = "";
    public bool OpenAiCompatible { get; init; }
    public string? DocsUrl { get; init; }
    /// <summary>ISO date (yyyy-MM-dd) this entry was last hand-verified, shown so stale entries are obvious rather than silently trusted.</summary>
    public required string LastVerified { get; init; }
    /// <summary>Set when FreeApiDiscoveryService's automated link check couldn't confirm SignupUrl/DocsUrl still resolve - a signal to re-verify by hand, not a confirmed dead link (bot-blocking gives false positives).</summary>
    public bool NeedsReview { get; init; }
    public string? NeedsReviewReason { get; init; }
}
