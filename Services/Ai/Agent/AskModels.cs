namespace AiPulse.Services.Ai.Agent;

public enum AskStatus
{
    /// <summary>Every sentence shown is supported by a cited passage.</summary>
    Answered,
    /// <summary>Greeting or thanks; no retrieval was needed.</summary>
    SmallTalk,
    /// <summary>The question was blocked by a guardrail.</summary>
    Refused,
    /// <summary>Nothing relevant was found, or nothing the model wrote could be verified.</summary>
    NotFound,
    RateLimited
}

public sealed record CitedSource(
    int Number,
    string Id,
    string Kind,
    string Title,
    string? Url,
    string Snippet,
    DateTimeOffset? Published);

public sealed record TraceStep(string Step, string Detail);

public sealed record AskResult(
    AskStatus Status,
    string Answer,
    IReadOnlyList<CitedSource> Sources,
    /// <summary>"model" when a language model wrote the answer, "extractive" when it was assembled from the passages themselves.</summary>
    string Mode,
    string? Provider,
    string? Model,
    int DroppedSentences,
    string? Notice,
    IReadOnlyList<TraceStep> Trace,
    TimeSpan Elapsed)
{
    public bool HasAnswer => Status is AskStatus.Answered or AskStatus.SmallTalk;
}
