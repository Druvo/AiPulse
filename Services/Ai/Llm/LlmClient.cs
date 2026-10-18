using Microsoft.Extensions.Options;

namespace AiPulse.Services.Ai.Llm;

/// <summary>
/// Tries each enabled provider in priority order and returns the first answer. Deliberately plain: routing rules,
/// rate limits, circuit breakers and a call log belong to the gateway layer built on top of <see cref="ILlmClient"/>.
/// </summary>
public sealed class LlmClient : ILlmClient
{
    private readonly IReadOnlyList<(ILlmProvider Provider, TimeSpan Timeout)> _providers;
    private readonly ILogger<LlmClient> _log;

    public LlmClient(IHttpClientFactory httpFactory, IOptions<LlmOptions> options, IConfiguration config, ILogger<LlmClient> log)
        : this(BuildProviders(httpFactory, options.Value, config), log)
    {
    }

    public LlmClient(IEnumerable<(ILlmProvider Provider, TimeSpan Timeout)> providers, ILogger<LlmClient> log)
    {
        _providers = providers.OrderBy(p => p.Provider.Priority).ThenBy(p => p.Provider.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log = log;
    }

    public bool HasProviders => _providers.Count > 0;

    public IReadOnlyList<LlmProviderInfo> Providers =>
        _providers.Select(p => new LlmProviderInfo(p.Provider.Name, p.Provider.Kind, p.Provider.Model, p.Provider.Priority)).ToList();

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default)
    {
        var attempts = new List<LlmAttempt>();

        foreach (var (provider, timeout) in _providers)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await provider.ChatAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = ex is OperationCanceledException ? $"timed out after {timeout.TotalSeconds:0}s" : ex.Message;
                _log.LogWarning("LLM provider {Provider} failed: {Reason}", provider.Name, reason);
                attempts.Add(new LlmAttempt(provider.Name, reason));
            }
        }

        throw new LlmUnavailableException(attempts);
    }

    private static List<(ILlmProvider, TimeSpan)> BuildProviders(IHttpClientFactory httpFactory, LlmOptions options, IConfiguration config)
    {
        var configured = options.Providers.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.BaseUrl)).ToList();

        if (configured.Count == 0)
        {
            configured.Add(new LlmProviderOptions
            {
                Name = "ollama",
                Kind = "ollama",
                BaseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434",
                Model = config["Ollama:ChatModel"] ?? "",
                Priority = 100
            });
        }

        var result = new List<(ILlmProvider, TimeSpan)>();
        for (var i = 0; i < configured.Count; i++)
        {
            var p = configured[i];
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"{p.Kind}-{i + 1}";

            ILlmProvider? provider = p.Kind.Trim().ToLowerInvariant() switch
            {
                "ollama" => new OllamaLlmProvider(httpFactory.CreateClient("llm"), p),
                "openai" or "openai-compatible" => new OpenAiCompatibleProvider(httpFactory.CreateClient("llm"), p),
                _ => null
            };

            if (provider is not null)
                result.Add((provider, TimeSpan.FromSeconds(Math.Clamp(p.TimeoutSeconds, 5, 600))));
        }

        return result;
    }
}
