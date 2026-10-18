namespace AiPulse.Services.Ai.Rag;

/// <summary>
/// One citable passage. <see cref="Text"/> is the evidence the citation checker compares answers against, so
/// anything an answer might quote (numbers, dates) has to be in it.
/// </summary>
public sealed record KnowledgeDocument(
    string Id,
    string Kind,
    string Title,
    string Text,
    string? Url,
    string Keywords = "",
    DateTimeOffset? Published = null,
    string? Source = null);

public sealed record ScoredDocument(KnowledgeDocument Doc, double Score, double Coverage, IReadOnlyList<string> MatchedTerms);
