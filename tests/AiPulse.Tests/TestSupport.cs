using System.Net;
using System.Text;
using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using AiPulse.Services.Ai.Rag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiPulse.Tests;

internal static class TestData
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiPulse.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (AiPulse.csproj).");
    }

    public static List<T> Load<T>(string dataFile) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(RepoRoot, "Data", dataFile)), Json) ?? new();

    public static KnowledgeInputs RealInputs(IEnumerable<FeedItem>? news = null) => new(
        Load<GlossaryTerm>("glossary.json"), Load<ToolEntry>("tools.json"), Load<PracticeTip>("practices.json"),
        Load<LearningModule>("learning.json"), Load<BenchmarkEntry>("benchmarks.json"), Load<CourseEntry>("courses.json"),
        Load<ModelDirectoryEntry>("model-directory.json"), news ?? Array.Empty<FeedItem>());

    public static KnowledgeCorpus RealCorpus(IEnumerable<FeedItem>? news = null)
    {
        var docs = KnowledgeCorpusBuilder.Build(RealInputs(news));
        return new KnowledgeCorpus(() => docs, TimeSpan.FromHours(1));
    }

    public static KnowledgeCorpus CorpusOf(params KnowledgeDocument[] docs) =>
        new(() => docs, TimeSpan.FromHours(1));

    public static RagAgent AgentFor(KnowledgeCorpus corpus, ILlmClient llm, AiOptions? options = null, TimeProvider? time = null) =>
        new(corpus, llm, new AskRateLimiter(), Options.Create(options ?? new AiOptions()), NullLogger<RagAgent>.Instance, time);

    public static KnowledgeDocument Doc(string id, string title, string text, string kind = "Glossary", string keywords = "", DateTimeOffset? published = null) =>
        new(id, kind, title, text, "/x", keywords, published);
}

/// <summary>A stand-in model that returns whatever the test scripts and remembers what it was asked.</summary>
internal sealed class FakeLlm : ILlmClient
{
    private readonly Func<LlmRequest, int, string> _reply;

    public FakeLlm(Func<LlmRequest, int, string> reply) => _reply = reply;
    public FakeLlm(string reply) : this((_, _) => reply) { }

    public List<LlmRequest> Requests { get; } = new();
    public bool HasProviders => true;
    public IReadOnlyList<LlmProviderInfo> Providers => new[] { new LlmProviderInfo("fake", "test", "fake-1", 0) };
    public IReadOnlyList<LlmProviderInfo> AllProviders => Providers;
    public IReadOnlyList<ProviderStats> Stats => Array.Empty<ProviderStats>();

    public Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmResponse(_reply(request, Requests.Count), "fake", "fake-1", 10, 5, TimeSpan.FromMilliseconds(5)));
    }
}

internal sealed class OfflineLlm : ILlmClient
{
    public bool HasProviders { get; init; } = true;
    public IReadOnlyList<LlmProviderInfo> Providers => Array.Empty<LlmProviderInfo>();
    public IReadOnlyList<LlmProviderInfo> AllProviders => Array.Empty<LlmProviderInfo>();
    public IReadOnlyList<ProviderStats> Stats => Array.Empty<ProviderStats>();

    public Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct = default) =>
        throw new LlmUnavailableException(new[] { new LlmAttempt("offline", "connection refused") });
}

internal sealed class StubProvider : ILlmProvider
{
    private readonly Func<LlmRequest, CancellationToken, Task<LlmResponse>> _handler;

    public StubProvider(string name, int priority, Func<LlmRequest, CancellationToken, Task<LlmResponse>> handler)
    {
        Name = name;
        Priority = priority;
        _handler = handler;
    }

    public string Name { get; }
    public string Kind => "stub";
    public string Model => "stub-model";
    public int Priority { get; }
    public int Calls { get; private set; }

    public Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        Calls++;
        return _handler(request, ct);
    }

    public static StubProvider Ok(string name, int priority, string text) =>
        new(name, priority, (_, _) => Task.FromResult(new LlmResponse(text, name, "stub-model", 1, 1, TimeSpan.Zero)));

    public static StubProvider Failing(string name, int priority, string error = "boom") =>
        new(name, priority, (_, _) => throw new HttpRequestException(error));
}

/// <summary>An HttpMessageHandler whose responses the test scripts, recording each request.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Seen.Add((request, body));
        return _respond(request);
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
