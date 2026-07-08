namespace AiPulse.Models;

/// <summary>A curated benchmark/leaderboard entry shown on the Explore page.</summary>
public sealed class BenchmarkEntry
{
    public required string Name { get; init; }
    public required string Category { get; init; } // Chat/Arena, Knowledge, Coding, Reasoning, Multimodal
    public required string Description { get; init; }
    public required string Url { get; init; }
}

/// <summary>A curated open-weight model entry for the "Try a model" directory.</summary>
public sealed class ModelDirectoryEntry
{
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string OneLiner { get; init; }
    public string? OllamaUrl { get; init; }
    public string? HuggingFaceUrl { get; init; }
    public string License { get; init; } = "";
    /// <summary>Rough RAM needed for the default (":latest") Ollama pull, at typical Q4 quantization.</summary>
    public double MinRamGb { get; init; }
    /// <summary>Actual download size in GB for the default (":latest") tag, from Ollama's registry manifest.</summary>
    public double SizeGb { get; init; }
    /// <summary>Groups the directory grid: General, Small & Fast, Coding, Vision, Embeddings, Multilingual…</summary>
    public string Category { get; init; } = "General";
}
