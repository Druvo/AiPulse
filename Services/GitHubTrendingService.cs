using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiPulse.Models;
using HtmlAgilityPack;

namespace AiPulse.Services;

/// <summary>
/// The "trending repos"/"trending developers" views scrape github.com/trending directly - GitHub has no
/// official API for either, and their unofficial trending page is the only place "stars today" and
/// contributor avatars exist at all. This is a deliberate trade-off (scraping the site is against the
/// letter of GitHub's ToS, though not their API) - see the ROADMAP for the full reality check. Keyword
/// search (<see cref="SearchAsync"/>) stays on the official Search API since trending pages aren't
/// searchable, and single-repo star lookups (<see cref="GetRepoStatsAsync"/>) use the official REST API
/// too - only the two trending views scrape HTML.
/// </summary>
public sealed class GitHubTrendingService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan RepoStatsCacheFor = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubTrendingService> _log;

    private static readonly string[] ValidSince = { "daily", "weekly", "monthly" };

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<TrendingRepo> Repos)> _repoCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new();

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<TrendingDeveloper> Devs)> _devCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _devLocks = new();

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int? Stars)> _repoStatsCache = new();

    public GitHubTrendingService(IHttpClientFactory httpFactory, ILogger<GitHubTrendingService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Same "since" values as github.com/trending itself: "daily" (default), "weekly", "monthly".</summary>
    public async Task<List<TrendingRepo>> GetTrendingReposAsync(string since = "daily", CancellationToken ct = default)
    {
        since = ValidSince.Contains(since) ? since : "daily";
        if (_repoCache.TryGetValue(since, out var cached) && DateTimeOffset.Now - cached.At < CacheFor)
            return cached.Repos;

        var gate = _repoLocks.GetOrAdd(since, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_repoCache.TryGetValue(since, out var cached2) && DateTimeOffset.Now - cached2.At < CacheFor)
                return cached2.Repos;

            var repos = await ScrapeTrendingReposAsync(since, ct);
            _repoCache[since] = (DateTimeOffset.Now, repos);
            return repos;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub trending-repo scrape failed for since={Since}", since);
            return _repoCache.TryGetValue(since, out var stale) ? stale.Repos : new();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Same "since" values as github.com/trending/developers itself: "daily" (default), "weekly", "monthly".</summary>
    public async Task<List<TrendingDeveloper>> GetTrendingDevelopersAsync(string since = "daily", CancellationToken ct = default)
    {
        since = ValidSince.Contains(since) ? since : "daily";
        if (_devCache.TryGetValue(since, out var cached) && DateTimeOffset.Now - cached.At < CacheFor)
            return cached.Devs;

        var gate = _devLocks.GetOrAdd(since, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_devCache.TryGetValue(since, out var cached2) && DateTimeOffset.Now - cached2.At < CacheFor)
                return cached2.Devs;

            var devs = await ScrapeTrendingDevelopersAsync(since, ct);
            _devCache[since] = (DateTimeOffset.Now, devs);
            return devs;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub trending-developers scrape failed for since={Since}", since);
            return _devCache.TryGetValue(since, out var stale) ? stale.Devs : new();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Live keyword search across repo name/description/README (not cached - the query space is unbounded). Uses the official Search API, not scraping.</summary>
    public async Task<List<TrendingRepo>> SearchAsync(string query, CancellationToken ct = default)
    {
        try
        {
            return await RunSearchQueriesAsync(new[] { Uri.EscapeDataString(query) }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub repo search for {Query} failed", query);
            return new();
        }
    }

    /// <summary>Current star count for one specific repo, via the official REST API (not scraping) - used to show a live popularity badge on the curated Tools &amp; Tips list. Null if the repo doesn't resolve.</summary>
    public async Task<int?> GetRepoStatsAsync(string owner, string repo, CancellationToken ct = default)
    {
        var key = $"{owner}/{repo}".ToLowerInvariant();
        if (_repoStatsCache.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.At < RepoStatsCacheFor)
            return cached.Stars;

        int? stars = null;
        try
        {
            var client = _httpFactory.CreateClient("explore");
            var json = await client.GetStringAsync($"https://api.github.com/repos/{owner}/{repo}", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("stargazers_count", out var s))
                stars = s.GetInt32();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GitHub repo stats lookup failed for {Owner}/{Repo}", owner, repo);
        }

        _repoStatsCache[key] = (DateTimeOffset.Now, stars);
        return stars;
    }

    private async Task<List<TrendingRepo>> ScrapeTrendingReposAsync(string since, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var html = await client.GetStringAsync($"https://github.com/trending?since={since}", ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var articles = doc.DocumentNode.SelectNodes("//article[contains(@class,'Box-row')]");
        if (articles is null) return new();

        var repos = new List<TrendingRepo>();
        foreach (var article in articles)
        {
            var nameLink = article.SelectSingleNode(".//h2[contains(@class,'lh-condensed')]//a");
            var fullName = DecodeAttr(nameLink?.GetAttributeValue("href", ""))?.Trim('/');
            if (string.IsNullOrWhiteSpace(fullName)) continue;

            var description = CleanInner(article.SelectSingleNode(".//p[contains(@class,'color-fg-muted')]")?.InnerText);
            var language = CleanInner(article.SelectSingleNode(".//span[@itemprop='programmingLanguage']")?.InnerText);
            var langStyle = article.SelectSingleNode(".//span[contains(@class,'repo-language-color')]")?.GetAttributeValue("style", "");
            var languageColor = ExtractHexColor(langStyle);

            var stars = ParseCount(article.SelectSingleNode(".//a[contains(@href,'/stargazers')]")?.InnerText);
            var forks = ParseCount(article.SelectSingleNode(".//a[contains(@href,'/forks')]")?.InnerText);
            var starsToday = ParseLeadingNumber(article.SelectSingleNode(".//div[contains(@class,'f6')]//span[contains(@class,'float-sm-right')]")?.InnerText);

            var contributors = new List<RepoContributor>();
            var contributorNodes = article.SelectNodes(".//a[@data-hovercard-type='user']/img[contains(@class,'avatar')]");
            if (contributorNodes is not null)
            {
                foreach (var img in contributorNodes)
                {
                    var username = DecodeAttr(img.GetAttributeValue("alt", ""))?.TrimStart('@') ?? "";
                    var avatarUrl = DecodeAttr(img.GetAttributeValue("src", "")) ?? "";
                    if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(avatarUrl))
                        contributors.Add(new RepoContributor(username, avatarUrl));
                }
            }

            repos.Add(new TrendingRepo
            {
                FullName = fullName,
                Description = description,
                Stars = stars,
                Forks = forks,
                StarsToday = starsToday,
                Language = language,
                LanguageColor = languageColor,
                Url = $"https://github.com/{fullName}",
                Contributors = contributors
            });
        }

        return repos;
    }

    private async Task<List<TrendingDeveloper>> ScrapeTrendingDevelopersAsync(string since, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var html = await client.GetStringAsync($"https://github.com/trending/developers?since={since}", ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var articles = doc.DocumentNode.SelectNodes("//article[contains(@class,'Box-row')]");
        if (articles is null) return new();

        var devs = new List<TrendingDeveloper>();
        foreach (var article in articles)
        {
            var avatarUrl = DecodeAttr(article.SelectSingleNode(".//img[contains(@class,'avatar-user')]")?.GetAttributeValue("src", ""));
            var username = CleanInner(article.SelectSingleNode(".//h1[contains(@class,'lh-condensed')]/a")?.InnerText);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(avatarUrl))
                continue;

            var repoLink = article.SelectSingleNode(".//h1[contains(@class,'h4') and contains(@class,'lh-condensed')]/a");
            var repoName = DecodeAttr(repoLink?.GetAttributeValue("href", ""))?.Trim('/');
            var repoDesc = CleanInner(article.SelectSingleNode(".//div[contains(@class,'f6') and contains(@class,'mt-1')]")?.InnerText);

            devs.Add(new TrendingDeveloper
            {
                Username = username,
                AvatarUrl = avatarUrl,
                PopularRepoName = string.IsNullOrWhiteSpace(repoName) ? null : repoName,
                PopularRepoUrl = string.IsNullOrWhiteSpace(repoName) ? null : $"https://github.com/{repoName}",
                PopularRepoDescription = repoDesc
            });
        }

        return devs;
    }

    /// <summary>HtmlAgilityPack doesn't decode entities in attribute values (e.g. GitHub's own markup writes avatar URLs as "...?s=40&amp;amp;v=4") - decode before treating as a real URL.</summary>
    private static string? DecodeAttr(string? raw) =>
        string.IsNullOrEmpty(raw) ? raw : System.Net.WebUtility.HtmlDecode(raw);

    private static string? CleanInner(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var decoded = System.Net.WebUtility.HtmlDecode(raw);
        var cleaned = Regex.Replace(decoded, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int ParseCount(string? raw)
    {
        var cleaned = CleanInner(raw)?.Replace(",", "");
        return int.TryParse(cleaned, out var n) ? n : 0;
    }

    private static int ParseLeadingNumber(string? raw)
    {
        if (raw is null) return 0;
        var m = Regex.Match(raw.Replace(",", ""), @"\d+");
        return m.Success ? int.Parse(m.Value) : 0;
    }

    private static string? ExtractHexColor(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return null;
        var m = Regex.Match(style, @"#[0-9a-fA-F]{3,6}");
        return m.Success ? m.Value : null;
    }

    private async Task<List<TrendingRepo>> RunSearchQueriesAsync(IEnumerable<string> queries, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var seen = new HashSet<string>();
        var repos = new List<TrendingRepo>();

        foreach (var q in queries)
        {
            var url = $"https://api.github.com/search/repositories?q={q}&sort=stars&order=desc&per_page=25";
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

        return repos.OrderByDescending(r => r.Stars).Take(60).ToList();
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
