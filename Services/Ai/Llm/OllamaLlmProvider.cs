using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiPulse.Services.Ai.Llm;

/// <summary>Chat against an Ollama server's native /api/chat (non-streaming).</summary>
public sealed class OllamaLlmProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly LlmProviderOptions _options;
    private string? _resolvedModel;

    public OllamaLlmProvider(HttpClient http, LlmProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => _options.Name;
    public string Kind => "ollama";
    public int Priority => _options.Priority;
    public string Model => string.IsNullOrWhiteSpace(_options.Model) ? (_resolvedModel ?? "(first installed)") : _options.Model;

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var model = await ResolveModelAsync(baseUrl, ct);

        var body = new
        {
            model,
            stream = false,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            options = new { temperature = request.Temperature, num_predict = request.MaxTokens }
        };

        var clock = Stopwatch.StartNew();
        using var resp = await _http.PostAsJsonAsync($"{baseUrl}/api/chat", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama answered {(int)resp.StatusCode}: {await Snippet(resp, ct)}");

        var dto = await resp.Content.ReadFromJsonAsync<ChatDto>(Json, ct);
        var text = dto?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Ollama returned an empty reply.");

        return new LlmResponse(text.Trim(), Name, model, dto!.PromptEvalCount, dto.EvalCount, clock.Elapsed);
    }

    private async Task<string> ResolveModelAsync(string baseUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.Model)) return _options.Model;
        if (_resolvedModel is not null) return _resolvedModel;

        var tags = await _http.GetFromJsonAsync<TagsDto>($"{baseUrl}/api/tags", Json, ct);
        var first = tags?.Models?.Select(m => m.Name ?? m.Model).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (first is null)
            throw new InvalidOperationException("Ollama has no models installed - pull one, or set Llm:Providers:N:Model.");

        return _resolvedModel = first;
    }

    private static async Task<string> Snippet(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        return text.Length <= 200 ? text : text[..200];
    }

    private sealed class ChatDto
    {
        public MessageDto? Message { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
        [JsonPropertyName("eval_count")] public int? EvalCount { get; set; }
    }

    private sealed class MessageDto { public string? Content { get; set; } }

    private sealed class TagsDto { public List<TagModelDto>? Models { get; set; } }

    private sealed class TagModelDto
    {
        public string? Name { get; set; }
        public string? Model { get; set; }
    }
}
