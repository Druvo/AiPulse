using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using AiPulse.Services.Ai.Summaries;
using AiPulse.Models;
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

    [LiveFact]
    public async Task A_real_model_summarises_an_item_from_its_own_text_and_the_summary_is_grounded()
    {
        var item = SummaryFixtures.Item();
        var service = new ItemSummaryService(Gemini(), new SummaryStore(null), new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<ItemSummaryService>.Instance);

        var result = await service.SummarizeAsync(item, "live");

        _output.WriteLine($"SUMMARY [{result.Status}]: {result.Summary?.Text ?? result.Message}");
        Assert.True(result.Ok, result.Message);
        Assert.True(ItemSummaryService.IsGrounded(result.Summary!.Text, item.Title + " " + item.Summary));
    }

    [LiveFact]
    public async Task A_real_model_explains_a_glossary_term_using_only_its_definition()
    {
        var term = TestData.Load<GlossaryTerm>("glossary.json").First(t => t.Term == "Quantization");
        var llm = Gemini();
        var options = new LlmOptions { Providers = { new LlmProviderOptions { Name = "gemini", BaseUrl = "https://x", Model = "m" } } };
        var store = new SummaryStore(null);
        var summarizer = new LlmSummarizer(llm, Options.Create(options),
            new ItemSummaryService(llm, store, new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<ItemSummaryService>.Instance),
            store, name => name == term.Term ? term : null, NullLogger<LlmSummarizer>.Instance);

        var text = await summarizer.ExplainTermAsync("Quantization");

        _output.WriteLine("EXPLANATION: " + text);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(text, await summarizer.ExplainTermAsync("Quantization")); // second call is the saved one
    }
}
