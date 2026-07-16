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
