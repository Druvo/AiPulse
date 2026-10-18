using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiPulse.Services.Ai.Llm;

/// <summary>Chat against any server that speaks the OpenAI /chat/completions API.</summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly LlmProviderOptions _options;

    public OpenAiCompatibleProvider(HttpClient http, LlmProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => _options.Name;
    public string Kind => "openai";
    public string Model => _options.Model;
    public int Priority => _options.Priority;

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new InvalidOperationException("An OpenAI-compatible provider needs a Model.");

        var url = _options.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens
        };
        if (!string.IsNullOrWhiteSpace(_options.ReasoningEffort))
            body["reasoning_effort"] = _options.ReasoningEffort;

        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var clock = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{Name} answered {(int)resp.StatusCode}: {(errorBody.Length <= 200 ? errorBody : errorBody[..200])}");
        }

        var dto = await resp.Content.ReadFromJsonAsync<CompletionDto>(Json, ct);
        var choice = dto?.Choices?.FirstOrDefault();
        var text = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(choice?.FinishReason == "length"
                ? $"{Name} used its whole token limit before writing any text (thinking models spend tokens on hidden reasoning) - set ReasoningEffort lower or allow more tokens."
                : $"{Name} returned an empty reply.");

        return new LlmResponse(text.Trim(), Name, dto!.Model ?? _options.Model, dto.Usage?.PromptTokens, dto.Usage?.CompletionTokens, clock.Elapsed);
    }

    private sealed class CompletionDto
    {
        public string? Model { get; set; }
        public List<ChoiceDto>? Choices { get; set; }
        public UsageDto? Usage { get; set; }
    }

    private sealed class ChoiceDto
    {
        public MessageDto? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class MessageDto { public string? Content { get; set; } }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    }
}
