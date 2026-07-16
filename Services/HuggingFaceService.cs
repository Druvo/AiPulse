using System.Text.Json;
using System.Text.Json.Serialization;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Fetches trending (and, on demand, keyword-searched) models/datasets from the public Hugging Face
/// Hub API (no auth required). The trending view is cached like FeedAggregatorService caches feeds;
/// searches always hit the API fresh since they're keyed by arbitrary user input. The cache is also
/// persisted to disk (App_Data/huggingface-cache.json) so a fetch failure - Hugging Face has been
/// observed to be intermittently unreachable from some networks, sometimes timing out completely -
/// falls back to the last successfully-fetched data instead of an empty "couldn't reach" result, even
/// across restarts.
/// </summary>
public sealed class HuggingFaceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PersistOpts = new() { WriteIndented = true };
    private const int ResultLimit = 60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HuggingFaceService> _log;
    private readonly string _cachePath;
    private readonly object _persistLock = new();

    private (DateTimeOffset At, List<TrendingModel> Models, List<TrendingDataset> Datasets)? _cache;
    private (DateTimeOffset At, List<TrendingModel> Models)? _ggufCache;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _ggufLock = new(1, 1);

    /// <summary>When the trending models/datasets were last successfully fetched (possibly from before this run) - null if never.</summary>
    public DateTimeOffset? LastUpdated => _cache?.At;
    /// <summary>When the GGUF trending list was last successfully fetched - null if never.</summary>
    public DateTimeOffset? GgufLastUpdated => _ggufCache?.At;

    public HuggingFaceService(IHttpClientFactory httpFactory, IWebHostEnvironment env, ILogger<HuggingFaceService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "huggingface-cache.json");
        LoadPersisted();
    }

    /// <summary>Pass force:true to bypass the freshness window and attempt a live fetch regardless of how recently one succeeded (manual "Refresh" buttons).</summary>
    public async Task<(List<TrendingModel> Models, List<TrendingDataset> Datasets)> GetTrendingAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is { } c && DateTimeOffset.Now - c.At < CacheFor)
            return (c.Models, c.Datasets);

        await _lock.WaitAsync(ct);
        try
        {
            if (!force && _cache is { } c2 && DateTimeOffset.Now - c2.At < CacheFor)
                return (c2.Models, c2.Datasets);

            var models = await FetchModelsAsync($"https://huggingface.co/api/models?sort=trendingScore&direction=-1&limit={ResultLimit}&full=true", ct);
            var datasets = await FetchDatasetsAsync($"https://huggingface.co/api/datasets?sort=trendingScore&direction=-1&limit={ResultLimit}&full=true", ct);

            _cache = (DateTimeOffset.Now, models, datasets);
            SavePersisted();
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
            var models = await FetchModelsAsync($"https://huggingface.co/api/models?search={q}&sort=trendingScore&direction=-1&limit={ResultLimit}&full=true", ct);
            var datasets = await FetchDatasetsAsync($"https://huggingface.co/api/datasets?search={q}&sort=trendingScore&direction=-1&limit={ResultLimit}&full=true", ct);
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
    public async Task<List<TrendingModel>> GetTrendingGgufAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _ggufCache is { } c && DateTimeOffset.Now - c.At < CacheFor)
            return c.Models;

        await _ggufLock.WaitAsync(ct);
        try
        {
            if (!force && _ggufCache is { } c2 && DateTimeOffset.Now - c2.At < CacheFor)
                return c2.Models;

            // Note: combining filter=gguf with sort=trendingScore silently drops the filter (verified against
            // the live API - it returns the generic trending list regardless of filter). sort=downloads is the
            // closest "popular" ordering that actually respects the filter.
            var models = await FetchModelsAsync($"https://huggingface.co/api/models?filter=gguf&sort=downloads&direction=-1&limit={ResultLimit}&full=true", ct);
            _ggufCache = (DateTimeOffset.Now, models);
            SavePersisted();
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
            return await FetchModelsAsync($"https://huggingface.co/api/models?filter=gguf&search={q}&sort=downloads&direction=-1&limit={ResultLimit}&full=true", ct);
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
            PipelineTag = m.PipelineTag,
            Author = m.Author,
            LibraryName = m.LibraryName,
            Tags = m.Tags ?? Array.Empty<string>(),
            LastModified = m.LastModified
        }).ToList();
    }

    private async Task<List<TrendingDataset>> FetchDatasetsAsync(string url, CancellationToken ct)
    {
        var dtos = await FetchAsync<HfDatasetDto>(url, ct);
        return dtos.Select(d => new TrendingDataset
        {
            Id = d.Id ?? "unknown",
            Likes = d.Likes,
            Downloads = d.Downloads,
            Author = d.Author,
            Tags = d.Tags ?? Array.Empty<string>(),
            LastModified = d.LastModified
        }).ToList();
    }

    private async Task<List<T>> FetchAsync<T>(string url, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var json = await client.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new();
    }

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var data = JsonSerializer.Deserialize<PersistedCache>(File.ReadAllText(_cachePath), JsonOpts);
            if (data is null) return;

            if (data.TrendingAt is { } at)
                _cache = (at, data.Models, data.Datasets);
            if (data.GgufAt is { } gat)
                _ggufCache = (gat, data.GgufModels);
        }
        catch { /* corrupt file -> start fresh, next successful fetch overwrites it */ }
    }

    private void SavePersisted()
    {
        lock (_persistLock)
        {
            var data = new PersistedCache
            {
                TrendingAt = _cache?.At,
                Models = _cache?.Models ?? new(),
                Datasets = _cache?.Datasets ?? new(),
                GgufAt = _ggufCache?.At,
                GgufModels = _ggufCache?.Models ?? new()
            };
            try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(data, PersistOpts)); }
            catch { /* best-effort - losing the persisted fallback isn't fatal, just less resilient */ }
        }
    }

    private sealed class PersistedCache
    {
        public DateTimeOffset? TrendingAt { get; set; }
        public List<TrendingModel> Models { get; set; } = new();
        public List<TrendingDataset> Datasets { get; set; } = new();
        public DateTimeOffset? GgufAt { get; set; }
        public List<TrendingModel> GgufModels { get; set; } = new();
    }

    private sealed class HfModelDto
    {
        public string? Id { get; set; }
        [JsonPropertyName("modelId")] public string? ModelId { get; set; }
        public int Likes { get; set; }
        public long Downloads { get; set; }
        [JsonPropertyName("pipeline_tag")] public string? PipelineTag { get; set; }
        public string? Author { get; set; }
        [JsonPropertyName("library_name")] public string? LibraryName { get; set; }
        public string[]? Tags { get; set; }
        public DateTimeOffset? LastModified { get; set; }
    }

    private sealed class HfDatasetDto
    {
        public string? Id { get; set; }
        public int Likes { get; set; }
        public long Downloads { get; set; }
        public string? Author { get; set; }
        public string[]? Tags { get; set; }
        public DateTimeOffset? LastModified { get; set; }
    }
}
