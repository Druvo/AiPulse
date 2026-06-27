namespace AiPulse.Models;

/// <summary>A curated learning module in the Learning Hub — a topic you should master, broken into steps.</summary>
public sealed class LearningModule
{
    public required string Title { get; init; }
    public required string Level { get; init; } // Beginner, Intermediate, Advanced
    public required string Why { get; init; }
    public LearningStep[] Steps { get; init; } = Array.Empty<LearningStep>();
    public string[] RelatedTerms { get; init; } = Array.Empty<string>();
}

/// <summary>One actionable step within a learning module.</summary>
public sealed class LearningStep
{
    public required string Text { get; init; }
    public string? Url { get; init; }
}
