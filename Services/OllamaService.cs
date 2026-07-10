using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Talks to a locally-running Ollama instance so models can be pulled and chatted with from AiPulse.
/// Base URL is configurable via appsettings ("Ollama:BaseUrl"), defaults to http://localhost:11434.
/// </summary>
public sealed class OllamaService
{
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OllamaService> _log;

    public OllamaService(IHttpClientFactory httpFactory, ILogger<OllamaService> log, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _log = log;
        _baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
    }

    /// <summary>Quick check - is Ollama installed and running right now? Fails fast (2s) if not.</summary>
    public async Task<bool> IsRunningAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var client = _httpFactory.CreateClient("ollama");
            var resp = await client.GetAsync($"{_baseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<OllamaModel>> GetInstalledModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _httpFactory.CreateClient("ollama");
            var result = await client.GetFromJsonAsync<TagsResponse>($"{_baseUrl}/api/tags", JsonOpts, ct);
            return (result?.Models ?? new()).Select(m => new OllamaModel
            {
                Name = m.Name ?? m.Model ?? "unknown",
                SizeBytes = m.Size,
                ParameterSize = m.Details?.ParameterSize,
                QuantizationLevel = m.Details?.QuantizationLevel,
                Family = m.Details?.Family,
                ContextLength = m.Details?.ContextLength,
                ModifiedAt = m.ModifiedAt
            }).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to list installed Ollama models");
            return new();
        }
    }

    /// <summary>
    /// Pulls (downloads) a model, invoking <paramref name="onProgress"/> for each streamed status line
    /// so the caller can update a progress bar. Ollama streams newline-delimited JSON objects.
    /// </summary>
    public async Task<bool> PullModelAsync(string modelName, Func<OllamaPullProgress, Task> onProgress, CancellationToken ct = default)
    {
        var client = _httpFactory.CreateClient("ollama");
        client.Timeout = TimeSpan.FromMinutes(30);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull")
        {
            Content = JsonContent.Create(new { model = modelName, stream = true })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var success = false;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var dto = JsonSerializer.Deserialize<PullProgressDto>(line, JsonOpts);
            if (dto is null) continue;

            await onProgress(new OllamaPullProgress { Status = dto.Status ?? "", Completed = dto.Completed, Total = dto.Total });
            if (dto.Status == "success") success = true;
        }
        return success;
    }

    public async Task<bool> DeleteModelAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            var client = _httpFactory.CreateClient("ollama");
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/delete")
            {
                Content = JsonContent.Create(new { model = modelName })
            };
            var resp = await client.SendAsync(request, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete Ollama model {Model}", modelName);
            return false;
        }
    }

    /// <summary>
    /// Sends the full conversation so far (multi-turn context) and streams the assistant's reply token
    /// by token via <paramref name="onChunk"/>, for a Playground-style live-typing feel instead of a
    /// single blocking wait. Returns the fully assembled reply plus generation stats (tokens/sec, total
    /// time) that Ollama reports on the final line of the stream.
    /// </summary>
    public async Task<OllamaChatResult> ChatStreamAsync(
        string modelName,
        IEnumerable<(string Role, string Content)> messages,
        Func<string, Task> onChunk,
        CancellationToken ct = default)
    {
        var client = _httpFactory.CreateClient("ollama");
        client.Timeout = TimeSpan.FromMinutes(10);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = modelName,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                stream = true
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var full = new StringBuilder();
        OllamaChatStats? stats = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var dto = JsonSerializer.Deserialize<ChatResponse>(line, JsonOpts);
            if (dto is null) continue;

            var chunk = dto.Message?.Content;
            if (!string.IsNullOrEmpty(chunk))
            {
                full.Append(chunk);
                await onChunk(chunk);
            }

            if (dto.Done)
            {
                stats = new OllamaChatStats
                {
                    PromptEvalCount = dto.PromptEvalCount,
                    PromptEvalDurationNs = dto.PromptEvalDuration,
                    EvalCount = dto.EvalCount,
                    EvalDurationNs = dto.EvalDuration,
                    LoadDurationNs = dto.LoadDuration,
                    TotalDurationNs = dto.TotalDuration
                };
            }
        }
        return new OllamaChatResult { Text = full.ToString(), Stats = stats };
    }

    private sealed class TagsResponse
    {
        public List<ModelDto>? Models { get; set; }
    }

    private sealed class ModelDto
    {
        public string? Name { get; set; }
        public string? Model { get; set; }
        public long Size { get; set; }
        public ModelDetailsDto? Details { get; set; }
        [JsonPropertyName("modified_at")] public DateTimeOffset? ModifiedAt { get; set; }
    }

    private sealed class ModelDetailsDto
    {
        [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
        [JsonPropertyName("quantization_level")] public string? QuantizationLevel { get; set; }
        public string? Family { get; set; }
        [JsonPropertyName("context_length")] public long? ContextLength { get; set; }
    }

    private sealed class PullProgressDto
    {
        public string? Status { get; set; }
        public long? Completed { get; set; }
        public long? Total { get; set; }
    }

    private sealed class ChatResponse
    {
        public ChatMessageDto? Message { get; set; }
        public bool Done { get; set; }
        [JsonPropertyName("prompt_eval_count")] public long? PromptEvalCount { get; set; }
        [JsonPropertyName("prompt_eval_duration")] public long? PromptEvalDuration { get; set; }
        [JsonPropertyName("eval_count")] public long? EvalCount { get; set; }
        [JsonPropertyName("eval_duration")] public long? EvalDuration { get; set; }
        [JsonPropertyName("load_duration")] public long? LoadDuration { get; set; }
        [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
    }

    private sealed class ChatMessageDto
    {
        public string? Content { get; set; }
    }
}
