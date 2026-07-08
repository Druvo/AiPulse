namespace AiPulse.Models;

/// <summary>A trending model from the Hugging Face Hub API.</summary>
public sealed class TrendingModel
{
    public required string Id { get; init; }
    public int Likes { get; init; }
    public long Downloads { get; init; }
    public string? PipelineTag { get; init; }
    public string Url => $"https://huggingface.co/{Id}";
}

/// <summary>A trending dataset from the Hugging Face Hub API.</summary>
public sealed class TrendingDataset
{
    public required string Id { get; init; }
    public int Likes { get; init; }
    public string Url => $"https://huggingface.co/datasets/{Id}";
}

/// <summary>A recently-popular AI-related repo, via GitHub's official Search API.</summary>
public sealed class TrendingRepo
{
    public required string FullName { get; init; }
    public string? Description { get; init; }
    public int Stars { get; init; }
    public string? Language { get; init; }
    public required string Url { get; init; }
}
