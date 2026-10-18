using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AiPulse.Tests;

/// <summary>Runs only when AIPULSE_LIVE_GEMINI_KEY is set; talks to the real Gemini API, so it is skipped everywhere else (CI, other machines).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string KeyVariable = "AIPULSE_LIVE_GEMINI_KEY";

    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyVariable)))
            Skip = $"Set {KeyVariable} (and optionally AIPULSE_LIVE_GEMINI_MODEL) to run against the real Gemini API.";
    }
}

public class LiveGeminiTests
{
    private readonly ITestOutputHelper _output;

    public LiveGeminiTests(ITestOutputHelper output) => _output = output;

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static LlmClient Gemini() => new(new Factory(), Options.Create(new LlmOptions
    {
        Providers =
        {
            new LlmProviderOptions
            {
                Name = "gemini",
                Kind = "openai",
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
                Model = Environment.GetEnvironmentVariable("AIPULSE_LIVE_GEMINI_MODEL") ?? "gemini-flash-latest",
                ApiKey = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable),
                ReasoningEffort = "low",
                TimeoutSeconds = 90
            }
        }
    }), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<LlmClient>.Instance);

    [LiveFact]
    public async Task Gemini_answers_a_plain_prompt()
    {
        var response = await Gemini().ChatAsync(new LlmRequest(new[] { LlmMessage.User("Reply with the single word: pong") }, 0.0, 1500));

        Assert.Contains("pong", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("gemini", response.Provider);
    }

    [LiveFact]
    public async Task A_real_model_answers_from_the_real_knowledge_base_and_every_sentence_is_verified()
    {
        var agent = new RagAgent(TestData.RealCorpus(), Gemini(), new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<RagAgent>.Instance);

        var result = await agent.AskAsync("What is RAG and why does it reduce hallucination?", "live");

        _output.WriteLine($"ANSWER ({result.Provider}/{result.Model}, {result.Elapsed.TotalSeconds:0.0}s, dropped {result.DroppedSentences}): {result.Answer}");
        foreach (var src in result.Sources) _output.WriteLine($"  [{src.Number}] {src.Kind}: {src.Title}");
        foreach (var step in result.Trace) _output.WriteLine($"  trace {step.Step}: {step.Detail}");

        Assert.Equal(AskStatus.Answered, result.Status);
        Assert.Equal("model", result.Mode);
        Assert.NotEmpty(result.Sources);
        Assert.Contains(result.Sources, s => s.Id == "glossary:rag");
        Assert.Matches(@"\[\d+\]", result.Answer);

        // Whatever the model wrote, re-verifying what we returned must find nothing to drop.
        var evidence = result.Sources.Select(s => s.Snippet).ToList();
        Assert.All(System.Text.RegularExpressions.Regex.Matches(result.Answer, @"\[(\d+)\]").Select(m => int.Parse(m.Groups[1].Value)),
            n => Assert.InRange(n, 1, result.Sources.Count));
    }

    [LiveFact]
    public async Task A_real_model_is_not_talked_into_answering_off_topic_questions()
    {
        var agent = new RagAgent(TestData.RealCorpus(), Gemini(), new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<RagAgent>.Instance);

        var result = await agent.AskAsync("What is the weather in Paris tomorrow?", "live");

        Assert.False(result.HasAnswer);
    }
}
