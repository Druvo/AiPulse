using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Ai.Llm;

/// <summary>
/// Tries each enabled provider in priority order and returns the first answer. Asks the <see cref="IAiGate"/> first, so the
/// admin's master switch and per-provider toggles take effect on the very next call, and keeps simple per-provider counters.
/// Routing rules, circuit breakers and per-person budgets are the next layer up.
/// </summary>
public sealed class LlmClient : ILlmClient
{
    private sealed class Counter
    {
        private readonly object _lock = new();
        private int _calls, _failures;
        private long _prompt, _completion;
        private TimeSpan _latency;
        private string? _lastError;
        private DateTimeOffset? _lastSuccess, _lastFailure;

        public void Success(LlmResponse r)
        {
            lock (_lock)
            {
                _calls++;
                _prompt += r.PromptTokens ?? 0;
                _completion += r.CompletionTokens ?? 0;
                _latency += r.Latency;
                _lastSuccess = DateTimeOffset.UtcNow;
            }
        }

        public void Failure(string error)
        {
            lock (_lock)
            {
                _calls++;
                _failures++;
                _lastError = error;
                _lastFailure = DateTimeOffset.UtcNow;
            }
        }

        public ProviderStats Snapshot(string name)
        {
            lock (_lock) return new ProviderStats(name, _calls, _failures, _prompt, _completion, _latency, _lastError, _lastSuccess, _lastFailure);
        }
    }

    private readonly IReadOnlyList<(ILlmProvider Provider, TimeSpan Timeout)> _providers;
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAiGate _gate;
    private readonly ILogger<LlmClient> _log;

    public LlmClient(IHttpClientFactory httpFactory, IOptions<LlmOptions> options, IConfiguration config, ILogger<LlmClient> log, IAiGate? gate = null)
        : this(BuildProviders(httpFactory, options.Value, config), log, gate)
    {
    }

    public LlmClient(IEnumerable<(ILlmProvider Provider, TimeSpan Timeout)> providers, ILogger<LlmClient> log, IAiGate? gate = null)
    {
        _providers = providers.OrderBy(p => p.Provider.Priority).ThenBy(p => p.Provider.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log = log;
        _gate = gate ?? OpenGate.Instance;
    }

    private IEnumerable<(ILlmProvider Provider, TimeSpan Timeout)> Active =>
        _gate.ModelCallsAllowed
            ? _providers.Where(p => _gate.IsProviderEnabled(p.Provider.Name))
            : Enumerable.Empty<(ILlmProvider, TimeSpan)>();

    public bool HasProviders => Active.Any();

    public IReadOnlyList<LlmProviderInfo> Providers => Active.Select(p => Info(p.Provider, enabled: true)).ToList();

    public IReadOnlyList<LlmProviderInfo> AllProviders =>
        _providers.Select(p => Info(p.Provider, _gate.IsProviderEnabled(p.Provider.Name))).ToList();

    public IReadOnlyList<ProviderStats> Stats =>
        _providers.Select(p => (_counters.TryGetValue(p.Provider.Name, out var c) ? c : new Counter()).Snapshot(p.Provider.Name)).ToList();

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!_gate.ModelCallsAllowed)
            throw new LlmUnavailableException("Model calls are switched off.");

        var active = Active.ToList();
        if (active.Count == 0)
            throw new LlmUnavailableException(_providers.Count == 0 ? "No language-model provider is configured." : "Every language-model provider is switched off.");

        var attempts = new List<LlmAttempt>();

        foreach (var (provider, timeout) in active)
        {
            var counter = _counters.GetOrAdd(provider.Name, _ => new Counter());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                var response = await provider.ChatAsync(request, cts.Token);
                counter.Success(response);
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = ex is OperationCanceledException ? $"timed out after {timeout.TotalSeconds:0}s" : ex.Message;
                _log.LogWarning("LLM provider {Provider} failed: {Reason}", provider.Name, reason);
                counter.Failure(reason);
                attempts.Add(new LlmAttempt(provider.Name, reason));
            }
        }

        throw new LlmUnavailableException(attempts);
    }

    private static LlmProviderInfo Info(ILlmProvider p, bool enabled) => new(p.Name, p.Kind, p.Model, p.Priority, enabled);

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
