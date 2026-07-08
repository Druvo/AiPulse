using System.Text.Json;
using System.Text.Json.Serialization;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Fetches trending (and, on demand, keyword-searched) models/datasets from the public Hugging Face
/// Hub API (no auth required). The trending view is cached like FeedAggregatorService caches feeds;
/// searches always hit the API fresh since they're keyed by arbitrary user input.
/// </summary>
public sealed class HuggingFaceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private const int ResultLimit = 60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HuggingFaceService> _log;

    private (DateTimeOffset At, List<TrendingModel> Models, List<TrendingDataset> Datasets)? _cache;
    private (DateTimeOffset At, List<TrendingModel> Models)? _ggufCache;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _ggufLock = new(1, 1);

    public HuggingFaceService(IHttpClientFactory httpFactory, ILogger<HuggingFaceService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<(List<TrendingModel> Models, List<TrendingDataset> Datasets)> GetTrendingAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTimeOffset.Now - c.At < CacheFor)
            return (c.Models, c.Datasets);

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && DateTimeOffset.Now - c2.At < CacheFor)
                return (c2.Models, c2.Datasets);

            var models = await FetchModelsAsync($"https://huggingface.co/api/models?sort=trendingScore&direction=-1&limit={ResultLimit}", ct);
            var datasets = await FetchDatasetsAsync($"https://huggingface.co/api/datasets?sort=trendingScore&direction=-1&limit={ResultLimit}", ct);

            _cache = (DateTimeOffset.Now, models, datasets);
            return (models, datasets);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hugging Face trending fetch failed");
            return (_cache?.Models ?? new(), _cache?.Datasets ?? new());
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Live keyword search (not cached - the query space is unbounded) across model/dataset names.</summary>
    public async Task<(List<TrendingModel> Models, List<TrendingDataset> Datasets)> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query);
        try
        {
            var models = await FetchModelsAsync($"https://huggingface.co/api/models?search={q}&sort=trendingScore&direction=-1&limit={ResultLimit}", ct);
            var datasets = await FetchDatasetsAsync($"https://huggingface.co/api/datasets?search={q}&sort=trendingScore&direction=-1&limit={ResultLimit}", ct);
            return (models, datasets);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hugging Face search for {Query} failed", query);
            return (new(), new());
        }
    }

    /// <summary>
    /// Trending models specifically packaged as GGUF - the format Ollama (and llama.cpp generally) can
    /// actually run, and pullable directly via "ollama pull hf.co/{id}" without needing to be in Ollama's
    /// own curated library. This is what makes the "Try a model" catalog genuinely open-ended instead of
    /// limited to a hand-picked list.
    /// </summary>
    public async Task<List<TrendingModel>> GetTrendingGgufAsync(CancellationToken ct = default)
    {
        if (_ggufCache is { } c && DateTimeOffset.Now - c.At < CacheFor)
            return c.Models;

        await _ggufLock.WaitAsync(ct);
        try
        {
            if (_ggufCache is { } c2 && DateTimeOffset.Now - c2.At < CacheFor)
                return c2.Models;

            // Note: combining filter=gguf with sort=trendingScore silently drops the filter (verified against
            // the live API - it returns the generic trending list regardless of filter). sort=downloads is the
            // closest "popular" ordering that actually respects the filter.
            var models = await FetchModelsAsync($"https://huggingface.co/api/models?filter=gguf&sort=downloads&direction=-1&limit={ResultLimit}", ct);
            _ggufCache = (DateTimeOffset.Now, models);
            return models;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hugging Face GGUF trending fetch failed");
            return _ggufCache?.Models ?? new();
        }
        finally
        {
            _ggufLock.Release();
        }
    }

    /// <summary>Live keyword search restricted to GGUF-packaged models (not cached).</summary>
    public async Task<List<TrendingModel>> SearchGgufAsync(string query, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(query);
        try
        {
            return await FetchModelsAsync($"https://huggingface.co/api/models?filter=gguf&search={q}&sort=downloads&direction=-1&limit={ResultLimit}", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hugging Face GGUF search for {Query} failed", query);
            return new();
        }
    }

    private async Task<List<TrendingModel>> FetchModelsAsync(string url, CancellationToken ct)
    {
        var dtos = await FetchAsync<HfModelDto>(url, ct);
        return dtos.Select(m => new TrendingModel
        {
            Id = m.Id ?? m.ModelId ?? "unknown",
            Likes = m.Likes,
            Downloads = m.Downloads,
            PipelineTag = m.PipelineTag
        }).ToList();
    }

    private async Task<List<TrendingDataset>> FetchDatasetsAsync(string url, CancellationToken ct)
    {
        var dtos = await FetchAsync<HfDatasetDto>(url, ct);
        return dtos.Select(d => new TrendingDataset { Id = d.Id ?? "unknown", Likes = d.Likes }).ToList();
    }

    private async Task<List<T>> FetchAsync<T>(string url, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var json = await client.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new();
    }

    private sealed class HfModelDto
    {
        public string? Id { get; set; }
        [JsonPropertyName("modelId")] public string? ModelId { get; set; }
        public int Likes { get; set; }
        public long Downloads { get; set; }
        [JsonPropertyName("pipeline_tag")] public string? PipelineTag { get; set; }
    }

    private sealed class HfDatasetDto
    {
        public string? Id { get; set; }
        public int Likes { get; set; }
    }
}
