using System.Text.Json;
using System.Text.Json.Serialization;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// GitHub has no official "trending repos" API - github.com/trending is an unofficial HTML page and
/// scraping it is fragile and against the letter of GitHub's ToS. This uses the official Search API
/// instead: recently-created AI-related repos sorted by stars, as a legitimate (if approximate)
/// substitute. The Search API has a stricter unauthenticated rate limit (10 req/min), so the trending
/// view is cached for an hour; keyword searches always hit the API fresh (unbounded query space).
/// </summary>
public sealed class GitHubTrendingService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Topics = { "llm", "ai-agents", "machine-learning", "generative-ai" };
    private const int PerTopicResults = 25;
    private const int TotalResultCap = 60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubTrendingService> _log;

    private (DateTimeOffset At, List<TrendingRepo> Repos)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GitHubTrendingService(IHttpClientFactory httpFactory, ILogger<GitHubTrendingService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<List<TrendingRepo>> GetTrendingReposAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTimeOffset.Now - c.At < CacheFor)
            return c.Repos;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && DateTimeOffset.Now - c2.At < CacheFor)
                return c2.Repos;

            var since = DateTimeOffset.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");
            var queries = Topics.Select(t => $"topic:{t}+created:>{since}");
            var ordered = await RunQueriesAsync(queries, ct);

            _cache = (DateTimeOffset.Now, ordered);
            return ordered;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub trending-repo search failed");
            return _cache?.Repos ?? new();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Live keyword search across repo name/description/README (not cached - the query space is unbounded).</summary>
    public async Task<List<TrendingRepo>> SearchAsync(string query, CancellationToken ct = default)
    {
        try
        {
            return await RunQueriesAsync(new[] { Uri.EscapeDataString(query) }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub repo search for {Query} failed", query);
            return new();
        }
    }

    private async Task<List<TrendingRepo>> RunQueriesAsync(IEnumerable<string> queries, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var seen = new HashSet<string>();
        var repos = new List<TrendingRepo>();

        foreach (var q in queries)
        {
            var url = $"https://api.github.com/search/repositories?q={q}&sort=stars&order=desc&per_page={PerTopicResults}";
            var json = await client.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<GhSearchResult>(json, JsonOpts);
            foreach (var item in result?.Items ?? new())
            {
                if (!seen.Add(item.FullName ?? "")) continue;
                repos.Add(new TrendingRepo
                {
                    FullName = item.FullName ?? "unknown",
                    Description = item.Description,
                    Stars = item.StargazersCount,
                    Language = item.Language,
                    Url = item.HtmlUrl ?? $"https://github.com/{item.FullName}"
                });
            }
        }

        return repos.OrderByDescending(r => r.Stars).Take(TotalResultCap).ToList();
    }

    private sealed class GhSearchResult
    {
        public List<GhRepoDto>? Items { get; set; }
    }

    private sealed class GhRepoDto
    {
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("stargazers_count")] public int StargazersCount { get; set; }
        public string? Language { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
