using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using AiPulse.Services.Ai.Rag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Ai.Evals;

/// <summary>
/// Runs a golden question set through the real pipeline (guardrails, retrieval, verification) against the real knowledge
/// corpus. Model replies are scripted per case, so a run is deterministic and needs no model - it measures AiPulse's own
/// logic, which is what a regression would break.
/// </summary>
public sealed class EvalRunner
{
    private readonly KnowledgeCorpus _corpus;
    private readonly AiOptions _options;

    public EvalRunner(KnowledgeCorpus corpus, AiOptions options)
    {
        _corpus = corpus;
        _options = options;
    }

    public async Task<EvalReport> RunAsync(EvalSuite suite, CancellationToken ct = default)
    {
        var failures = new List<EvalFailure>();
        var totals = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in suite.Cases)
        {
            var problems = await CheckAsync(c, ct);
            totals.TryGetValue(c.Category, out var t);
            totals[c.Category] = (t.Passed + (problems.Count == 0 ? 1 : 0), t.Total + 1);
            failures.AddRange(problems.Select(p => new EvalFailure(c.Id, c.Category, p)));
        }

        var scores = totals
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new CategoryScore(kv.Key, kv.Value.Passed, kv.Value.Total,
                suite.Thresholds.TryGetValue(kv.Key, out var th) ? th : 1.0))
            .ToList();

        return new EvalReport(scores, failures);
    }

    private async Task<List<string>> CheckAsync(EvalCase c, CancellationToken ct)
    {
        var problems = new List<string>();

        if (c.ExpectSources is { Count: > 0 } || c.ExpectTop is not null)
        {
            var ranked = _corpus.Search(c.Question, Math.Max(c.Within, 1), 0).Select(h => h.Doc.Id).ToList();

            foreach (var id in c.ExpectSources ?? new())
                if (!ranked.Contains(id))
                    problems.Add($"expected {id} within the top {c.Within}, got [{string.Join(", ", ranked)}]");

            if (c.ExpectTop is not null && ranked.FirstOrDefault() != c.ExpectTop)
                problems.Add($"expected {c.ExpectTop} first, got {ranked.FirstOrDefault() ?? "nothing"}");
        }

        if (c.ExpectStatus is null && c.ExpectNotStatus is null && c.ExpectContains is null && c.ExpectAbsent is null && c.ExpectDroppedMin is null && c.ExpectDroppedMax is null)
            return problems;

        ILlmClient llm = c.ModelReply is null ? new NoModelClient() : new ScriptedModelClient(c.ModelReply);
        var agent = new RagAgent(_corpus, llm, new AskRateLimiter(), Options.Create(_options), NullLogger<RagAgent>.Instance);
        var result = await agent.AskAsync(c.Question, "eval:" + c.Id, ct);

        if (c.ExpectStatus is not null && !string.Equals(result.Status.ToString(), c.ExpectStatus, StringComparison.OrdinalIgnoreCase))
            problems.Add($"expected status {c.ExpectStatus}, got {result.Status}");

        if (c.ExpectNotStatus is not null && string.Equals(result.Status.ToString(), c.ExpectNotStatus, StringComparison.OrdinalIgnoreCase))
            problems.Add($"status must not be {c.ExpectNotStatus}");

        foreach (var needle in c.ExpectContains ?? new())
            if (!result.Answer.Contains(needle, StringComparison.OrdinalIgnoreCase))
                problems.Add($"answer should contain \"{needle}\"");

        foreach (var needle in c.ExpectAbsent ?? new())
            if (result.Answer.Contains(needle, StringComparison.OrdinalIgnoreCase))
                problems.Add($"answer must not contain \"{needle}\"");

        if (c.ExpectDroppedMin is { } min && result.DroppedSentences < min)
            problems.Add($"expected at least {min} dropped sentence(s), got {result.DroppedSentences}");

        if (c.ExpectDroppedMax is { } max && result.DroppedSentences > max)
            problems.Add($"expected at most {max} dropped sentence(s), got {result.DroppedSentences}");

        return problems;
    }

    private sealed class NoModelClient : ILlmClient
    {
        public bool HasProviders => false;
        public IReadOnlyList<LlmProviderInfo> Providers => Array.Empty<LlmProviderInfo>();
        public Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new LlmUnavailableException(Array.Empty<LlmAttempt>());
    }

    private sealed class ScriptedModelClient : ILlmClient
    {
        private readonly string _reply;
        public ScriptedModelClient(string reply) => _reply = reply;
        public bool HasProviders => true;
        public IReadOnlyList<LlmProviderInfo> Providers => new[] { new LlmProviderInfo("scripted", "eval", "scripted", 0) };
        public Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse(_reply, "scripted", "scripted", null, null, TimeSpan.Zero));
    }
}
