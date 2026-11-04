using AiPulse.Models;
using AiPulse.Services.Ai.Summaries;

namespace AiPulse.Services;

/// <summary>
/// The optional AI layer: short summaries and explanations written by a language model from AiPulse's own text and
/// checked against it. The dashboard works fully without it; it is on only when an LLM provider is configured
/// (see <c>Llm:Providers</c>), implemented by <see cref="LlmSummarizer"/>.
/// </summary>
public interface ISummarizer
{
    /// <summary>Whether any AI feature is available: a provider is configured and switched on, model calls are allowed, and at least one feature is on.</summary>
    bool Enabled { get; }

    /// <summary>Whether the per-item Summarize button should be offered.</summary>
    bool SummariesEnabled { get; }

    /// <summary>Whether the glossary's "Explain with AI" button should be offered.</summary>
    bool ExplanationsEnabled { get; }

    /// <summary>One or two sentences about a single item, from that item's own text. <paramref name="callerKey"/> identifies the person for rate limiting.</summary>
    Task<ItemSummaryResult> SummarizeItemAsync(FeedItem item, string callerKey, CancellationToken ct = default);

    /// <summary>Summarize recent feed items into a short digest. Returns null when disabled or nothing could be verified.</summary>
    Task<string?> SummarizeDigestAsync(IReadOnlyList<FeedItem> items, CancellationToken ct = default);

    /// <summary>Explain a glossary term in plain language. Returns null when disabled or the term is unknown.</summary>
    Task<string?> ExplainTermAsync(string term, CancellationToken ct = default);
}
