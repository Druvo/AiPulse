namespace AiPulse.Services.Ai.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
}

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    double Temperature = 0.1,
    int MaxTokens = 700);

public sealed record LlmResponse(
    string Text,
    string Provider,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Latency);

/// <summary>One provider that was tried and why it didn't produce an answer.</summary>
public sealed record LlmAttempt(string Provider, string Error);

public sealed class LlmUnavailableException : Exception
{
    public IReadOnlyList<LlmAttempt> Attempts { get; }

    public LlmUnavailableException(IReadOnlyList<LlmAttempt> attempts)
        : base(attempts.Count == 0
            ? "No language-model provider is configured."
            : "No language-model provider could answer: " + string.Join("; ", attempts.Select(a => $"{a.Provider}: {a.Error}")))
    {
        Attempts = attempts;
    }

    /// <summary>For when nothing was tried because model calls are switched off or every provider is.</summary>
    public LlmUnavailableException(string message) : base(message)
    {
        Attempts = Array.Empty<LlmAttempt>();
    }
}

/// <summary>What the rest of the app talks to. A gateway (rate limits, routing, call log) can wrap or replace this without callers changing.</summary>
public interface ILlmClient
{
    /// <summary>True if model calls are allowed and at least one provider is switched on - not whether it is reachable right now.</summary>
    bool HasProviders { get; }

    /// <summary>The providers that would be tried, in order.</summary>
    IReadOnlyList<LlmProviderInfo> Providers { get; }

    /// <summary>Every configured provider, including ones an admin has switched off.</summary>
    IReadOnlyList<LlmProviderInfo> AllProviders { get; }

    /// <summary>Calls, failures, tokens and latency per provider since the app started.</summary>
    IReadOnlyList<ProviderStats> Stats { get; }

    Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default);
}

public interface ILlmProvider
{
    string Name { get; }
    string Kind { get; }
    string Model { get; }
    int Priority { get; }
    Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>Provider details that are safe to show in the UI (never the API key).</summary>
public sealed record LlmProviderInfo(string Name, string Kind, string Model, int Priority, bool Enabled = true);

/// <summary>Counters since the app started (not persisted).</summary>
public sealed record ProviderStats(
    string Name,
    int Calls,
    int Failures,
    long PromptTokens,
    long CompletionTokens,
    TimeSpan TotalLatency,
    string? LastError,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt)
{
    public int Successes => Calls - Failures;
    public TimeSpan AverageLatency => Successes <= 0 ? TimeSpan.Zero : TotalLatency / Successes;
}
