namespace AiPulse.Models;

/// <summary>A trending model from the Hugging Face Hub API.</summary>
public sealed class TrendingModel
{
    public required string Id { get; init; }
    public int Likes { get; init; }
    /// <summary>Downloads in the last 30 days - the only download figure the public Hub API exposes (there is no separate "all-time" counter).</summary>
    public long Downloads { get; init; }
    public string? PipelineTag { get; init; }
    /// <summary>The org/user that owns the repo, e.g. "meta-llama" - null if the API didn't report one.</summary>
    public string? Author { get; init; }
    /// <summary>Framework the model card declares, e.g. "transformers", "diffusers", "gguf" - null if unset.</summary>
    public string? LibraryName { get; init; }
    /// <summary>Raw tag list from the Hub (includes framework/license/language tags mixed together).</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset? LastModified { get; init; }
    /// <summary>Extracted from a "license:xxx" tag, if present.</summary>
    public string? License => Tags.FirstOrDefault(t => t.StartsWith("license:"))?["license:".Length..];
    public string Url => $"https://huggingface.co/{Id}";
}

/// <summary>A trending dataset from the Hugging Face Hub API.</summary>
public sealed class TrendingDataset
{
    public required string Id { get; init; }
    public int Likes { get; init; }
    public long Downloads { get; init; }
    public string? Author { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset? LastModified { get; init; }
    public string? License => Tags.FirstOrDefault(t => t.StartsWith("license:"))?["license:".Length..];
    public string Url => $"https://huggingface.co/datasets/{Id}";
}

/// <summary>A repo on GitHub's real Trending page (scraped - see GitHubTrendingService for the ToS trade-off).</summary>
public sealed class TrendingRepo
{
    public required string FullName { get; init; }
    public string? Description { get; init; }
    public int Stars { get; init; }
    public int Forks { get; init; }
    /// <summary>"N stars today" from the trending page - 0 when not scraped (e.g. a keyword search result).</summary>
    public int StarsToday { get; init; }
    public string? Language { get; init; }
    /// <summary>Hex color GitHub itself uses for this language's dot (from the page's own inline style), e.g. "#3178c6".</summary>
    public string? LanguageColor { get; init; }
    public required string Url { get; init; }
    public List<RepoContributor> Contributors { get; init; } = new();
}

/// <summary>One of the "Built by" avatars on a trending repo's card.</summary>
public sealed record RepoContributor(string Username, string AvatarUrl)
{
    public string ProfileUrl => $"https://github.com/{Username}";
}

/// <summary>One day's trending-repos leaderboard for a given since-window, persisted so past days stay browsable after the live scrape cache moves on.</summary>
public sealed class TrendingRepoSnapshot
{
    public required DateOnly Date { get; init; }
    public required string Since { get; init; } // daily, weekly, monthly
    public List<TrendingRepo> Repos { get; init; } = new();
}

/// <summary>A developer on GitHub's real Trending Developers page (scraped).</summary>
public sealed class TrendingDeveloper
{
    public required string Username { get; init; }
    public required string AvatarUrl { get; init; }
    public string? PopularRepoName { get; init; }
    public string? PopularRepoUrl { get; init; }
    public string? PopularRepoDescription { get; init; }
    public string ProfileUrl => $"https://github.com/{Username}";
}
