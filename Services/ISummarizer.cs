using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Optional AI layer. The dashboard works fully without it. To enable AI later,
/// implement this interface (e.g. calling the Claude API) and register it in Program.cs
/// in place of NullSummarizer.
/// </summary>
public interface ISummarizer
{
    /// <summary>Whether an AI backend is actually wired up.</summary>
    bool Enabled { get; }

    /// <summary>Summarize the day's feed items into a short digest. Returns null when disabled.</summary>
    Task<string?> SummarizeDigestAsync(IReadOnlyList<FeedItem> items, CancellationToken ct = default);

    /// <summary>Explain a glossary term in plain language. Returns null when disabled.</summary>
    Task<string?> ExplainTermAsync(string term, CancellationToken ct = default);
}

/// <summary>Default no-op implementation: keeps the app standalone with zero API keys.</summary>
public sealed class NullSummarizer : ISummarizer
{
    public bool Enabled => false;
    public Task<string?> SummarizeDigestAsync(IReadOnlyList<FeedItem> items, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<string?> ExplainTermAsync(string term, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
