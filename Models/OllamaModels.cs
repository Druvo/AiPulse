namespace AiPulse.Models;

/// <summary>A model currently pulled/installed in the user's local Ollama.</summary>
public sealed class OllamaModel
{
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
    public string? ParameterSize { get; init; }
    public string? QuantizationLevel { get; init; }
    public string? Family { get; init; }
    public long? ContextLength { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }

    public string SizeDisplay => SizeBytes <= 0 ? "" : $"{SizeBytes / 1_000_000_000.0:0.#} GB";
}

/// <summary>One line of progress while pulling a model (Ollama streams these as it downloads layers).</summary>
public sealed class OllamaPullProgress
{
    public string Status { get; init; } = "";
    public long? Completed { get; init; }
    public long? Total { get; init; }

    public int? PercentComplete => Total is > 0 && Completed is not null
        ? (int)(100.0 * Completed.Value / Total.Value)
        : null;
}

/// <summary>Generation stats Ollama reports on the final line of a chat stream - free performance telemetry.</summary>
public sealed class OllamaChatStats
{
    public long? PromptEvalCount { get; init; }
    public long? PromptEvalDurationNs { get; init; }
    public long? EvalCount { get; init; }
    public long? EvalDurationNs { get; init; }
    public long? LoadDurationNs { get; init; }
    public long? TotalDurationNs { get; init; }

    /// <summary>Output tokens/sec - the generation-speed number people usually mean by "tok/s".</summary>
    public double? TokensPerSecond => EvalCount is > 0 && EvalDurationNs is > 0
        ? Math.Round(EvalCount.Value / (EvalDurationNs.Value / 1_000_000_000.0), 1)
        : null;

    public double? TotalSeconds => TotalDurationNs is not null
        ? Math.Round(TotalDurationNs.Value / 1_000_000_000.0, 1)
        : null;

    public double? LoadSeconds => LoadDurationNs is not null
        ? Math.Round(LoadDurationNs.Value / 1_000_000_000.0, 1)
        : null;

    public double? GenerateSeconds => EvalDurationNs is not null
        ? Math.Round(EvalDurationNs.Value / 1_000_000_000.0, 1)
        : null;
}

/// <summary>The full assembled reply from a streamed chat call, plus generation stats from the final line.</summary>
public sealed class OllamaChatResult
{
    public required string Text { get; init; }
    public OllamaChatStats? Stats { get; init; }
}
