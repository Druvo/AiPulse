using AiPulse.Services.Ai;
using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using AiPulse.Services.Ai.Rag;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiPulse.Tests;

public class AiSettingsServiceTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"aipulse-ai-{Guid.NewGuid():N}", "ai-settings.json");

    [Fact]
    public void Everything_is_on_by_default()
    {
        var gate = new AiSettingsService(null);

        Assert.True(gate.ModelCallsAllowed);
        Assert.True(gate.AskMayUseModel);
        Assert.True(gate.SummariesAllowed);
        Assert.True(gate.ExplanationsAllowed);
        Assert.True(gate.IsProviderEnabled("anything"));
    }

    [Fact]
    public void The_master_switch_turns_every_feature_off_at_once()
    {
        var gate = new AiSettingsService(null);

        gate.Update(s => s with { Enabled = false });

        Assert.False(gate.ModelCallsAllowed);
        Assert.False(gate.AskMayUseModel);
        Assert.False(gate.SummariesAllowed);
        Assert.False(gate.ExplanationsAllowed);
    }

    [Fact]
    public void Each_feature_can_be_switched_off_on_its_own()
    {
        var gate = new AiSettingsService(null);

        gate.Update(s => s with { Summaries = false });
        Assert.True(gate.AskMayUseModel);
        Assert.False(gate.SummariesAllowed);
        Assert.True(gate.ExplanationsAllowed);

        gate.Update(s => s with { Summaries = true, AskUsesModel = false });
        Assert.False(gate.AskMayUseModel);
        Assert.True(gate.SummariesAllowed);
        Assert.True(gate.ModelCallsAllowed);
    }

    [Fact]
    public void A_provider_can_be_switched_off_and_on_by_name_ignoring_case()
    {
        var gate = new AiSettingsService(null);

        gate.SetProviderEnabled("Gemini", false);
        gate.SetProviderEnabled("gemini", false); // twice: still listed once

        Assert.False(gate.IsProviderEnabled("GEMINI"));
        Assert.True(gate.IsProviderEnabled("ollama"));
        Assert.Single(gate.Current.DisabledProviders);

        gate.SetProviderEnabled("gemini", true);
        Assert.True(gate.IsProviderEnabled("gemini"));
        Assert.Empty(gate.Current.DisabledProviders);
    }

    [Fact]
    public void Configuration_overrides_the_saved_switches_and_says_so()
    {
        var gate = new AiSettingsService(null, configurationAllows: false);

        gate.Update(s => s with { Enabled = true });

        Assert.True(gate.ForcedOffByConfiguration);
        Assert.False(gate.ModelCallsAllowed);
        Assert.False(gate.SummariesAllowed);
    }

    [Fact]
    public void Switches_are_saved_and_survive_a_restart()
    {
        var path = TempFile();
        try
        {
            var first = new AiSettingsService(path);
            first.Update(s => s with { Enabled = false, Explanations = false });
            first.SetProviderEnabled("stub", false);

            var second = new AiSettingsService(path);

            Assert.False(second.Current.Enabled);
            Assert.False(second.Current.Explanations);
            Assert.True(second.Current.Summaries);
            Assert.False(second.IsProviderEnabled("stub"));
        }
        finally { var dir = Path.GetDirectoryName(path)!; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_damaged_settings_file_falls_back_to_the_defaults_instead_of_stopping_the_app()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            var gate = new AiSettingsService(path);

            Assert.True(gate.ModelCallsAllowed);
        }
        finally { var dir = Path.GetDirectoryName(path)!; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_change_raises_the_Changed_event()
    {
        var gate = new AiSettingsService(null);
        var raised = 0;
        gate.Changed += () => raised++;

        gate.Update(s => s with { Enabled = false });
        gate.SetProviderEnabled("x", false);

        Assert.Equal(2, raised);
    }
}

public class LlmClientGateTests
{
    private static readonly LlmRequest Ask = new(new[] { LlmMessage.User("hi") });

    private static LlmClient Client(AiSettingsService gate, params StubProvider[] providers) =>
        new(providers.Select(p => ((ILlmProvider)p, TimeSpan.FromSeconds(5))), NullLogger<LlmClient>.Instance, gate);

    [Fact]
    public async Task With_the_master_switch_off_no_provider_is_called()
    {
        var gate = new AiSettingsService(null);
        var provider = StubProvider.Ok("p", 1, "answer");
        var client = Client(gate, provider);
        gate.Update(s => s with { Enabled = false });

        Assert.False(client.HasProviders);
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));

        Assert.Contains("switched off", ex.Message);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_switched_off_provider_is_skipped_and_the_next_one_answers()
    {
        var gate = new AiSettingsService(null);
        var first = StubProvider.Ok("first", 1, "from first");
        var second = StubProvider.Ok("second", 2, "from second");
        var client = Client(gate, first, second);

        gate.SetProviderEnabled("first", false);
        var response = await client.ChatAsync(Ask);

        Assert.Equal("second", response.Provider);
        Assert.Equal(0, first.Calls);
        Assert.Equal(new[] { "second" }, client.Providers.Select(p => p.Name));
    }

    [Fact]
    public async Task When_every_provider_is_switched_off_that_is_what_the_error_says()
    {
        var gate = new AiSettingsService(null);
        var client = Client(gate, StubProvider.Ok("a", 1, "x"), StubProvider.Ok("b", 2, "x"));
        gate.SetProviderEnabled("a", false);
        gate.SetProviderEnabled("b", false);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));

        Assert.Contains("Every language-model provider is switched off", ex.Message);
        Assert.False(client.HasProviders);
    }

    [Fact]
    public void All_providers_lists_switched_off_ones_with_their_state()
    {
        var gate = new AiSettingsService(null);
        var client = Client(gate, StubProvider.Ok("a", 1, "x"), StubProvider.Ok("b", 2, "x"));
        gate.SetProviderEnabled("b", false);

        Assert.Equal(new[] { ("a", true), ("b", false) }, client.AllProviders.Select(p => (p.Name, p.Enabled)));
        Assert.Equal(new[] { "a" }, client.Providers.Select(p => p.Name));
    }

    [Fact]
    public async Task Switching_back_on_works_without_rebuilding_anything()
    {
        var gate = new AiSettingsService(null);
        var client = Client(gate, StubProvider.Ok("p", 1, "answer"));

        gate.Update(s => s with { Enabled = false });
        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));
        gate.Update(s => s with { Enabled = true });

        Assert.Equal("answer", (await client.ChatAsync(Ask)).Text);
    }

    [Fact]
    public async Task Calls_failures_tokens_and_latency_are_counted_per_provider()
    {
        var failing = StubProvider.Failing("flaky", 1, "503 high demand");
        var working = new StubProvider("good", 2, (_, _) => Task.FromResult(new LlmResponse("ok", "good", "m", 100, 20, TimeSpan.FromSeconds(2))));
        var untouched = StubProvider.Ok("idle", 3, "x");
        var client = Client(new AiSettingsService(null), failing, working, untouched);

        await client.ChatAsync(Ask);
        await client.ChatAsync(Ask);

        var stats = client.Stats.ToDictionary(s => s.Name);
        Assert.Equal((2, 2), (stats["flaky"].Calls, stats["flaky"].Failures));
        Assert.Equal("503 high demand", stats["flaky"].LastError);
        Assert.NotNull(stats["flaky"].LastFailureAt);

        Assert.Equal((2, 0, 200L, 40L), (stats["good"].Calls, stats["good"].Failures, stats["good"].PromptTokens, stats["good"].CompletionTokens));
        Assert.Equal(TimeSpan.FromSeconds(2), stats["good"].AverageLatency);
        Assert.NotNull(stats["good"].LastSuccessAt);

        Assert.Equal(0, stats["idle"].Calls);
        Assert.Equal(TimeSpan.Zero, stats["idle"].AverageLatency);
    }

    [Fact]
    public async Task A_call_refused_by_the_gate_is_not_counted_as_a_provider_failure()
    {
        var gate = new AiSettingsService(null);
        var client = Client(gate, StubProvider.Ok("p", 1, "x"));
        gate.Update(s => s with { Enabled = false });

        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));

        Assert.Equal(0, client.Stats.Single().Calls);
    }
}

public class AskAndSummarizerSwitchTests
{
    private static readonly KnowledgeDocument Caching = TestData.Doc("g:caching", "Prompt caching",
        "Reusing a processed prompt prefix often cuts cost on the cached portion by about 90% and reduces latency.");

    private const string Question = "How much does prompt caching cut cost?";

    private static RagAgent Agent(ILlmClient llm, IAiGate gate) =>
        new(TestData.CorpusOf(Caching), llm, new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<RagAgent>.Instance, null, gate);

    [Fact]
    public async Task With_Asks_model_switch_off_it_quotes_the_sources_and_never_calls_the_model()
    {
        var gate = new AiSettingsService(null);
        gate.Update(s => s with { AskUsesModel = false });
        var llm = new FakeLlm("should not be used");

        var result = await Agent(llm, gate).AskAsync(Question, "u");

        Assert.Equal(AskStatus.Answered, result.Status);
        Assert.Equal("extractive", result.Mode);
        Assert.Contains("switched off", result.Notice);
        Assert.Contains("90%", result.Answer);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task With_the_master_switch_off_Ask_also_falls_back_to_quoting()
    {
        var gate = new AiSettingsService(null);
        gate.Update(s => s with { Enabled = false });
        var llm = new FakeLlm("should not be used");

        var result = await Agent(llm, gate).AskAsync(Question, "u");

        Assert.Equal("extractive", result.Mode);
        Assert.Contains("switched off", result.Notice);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Switching_it_back_on_makes_the_next_question_use_the_model_again()
    {
        var gate = new AiSettingsService(null);
        var llm = new FakeLlm("Prompt caching cuts cost by about 90% [1].");
        var agent = Agent(llm, gate);

        gate.Update(s => s with { AskUsesModel = false });
        Assert.Equal("extractive", (await agent.AskAsync(Question, "u")).Mode);
        gate.Update(s => s with { AskUsesModel = true });
        Assert.Equal("model", (await agent.AskAsync(Question, "u")).Mode);
    }

    [Fact]
    public async Task Providers_that_exist_but_are_all_switched_off_say_switched_off_not_not_configured()
    {
        var gate = new AiSettingsService(null);
        var client = new LlmClient(new[] { ((ILlmProvider)StubProvider.Ok("p", 1, "x"), TimeSpan.FromSeconds(5)) }, NullLogger<LlmClient>.Instance, gate);
        gate.SetProviderEnabled("p", false);

        var result = await Agent(client, gate).AskAsync(Question, "u");

        Assert.Contains("switched off", result.Notice);
        Assert.DoesNotContain("not configured", result.Notice);
    }

    [Fact]
    public async Task With_nothing_configured_at_all_it_still_says_not_configured()
    {
        var result = await Agent(new OfflineLlm { HasProviders = false }, OpenGate.Instance).AskAsync(Question, "u");
        Assert.Contains("No language model is configured", result.Notice);
    }

    private static AiPulse.Services.Ai.Summaries.LlmSummarizer Summarizer(FakeLlm llm, IAiGate gate, bool configured = true)
    {
        var options = new LlmOptions();
        if (configured) options.Providers.Add(new LlmProviderOptions { Name = "p", Kind = "openai", BaseUrl = "https://x/v1", Model = "m" });
        var store = new AiPulse.Services.Ai.Summaries.SummaryStore(null);
        var items = new AiPulse.Services.Ai.Summaries.ItemSummaryService(llm, store, new AskRateLimiter(), Options.Create(new AiOptions()), NullLogger<AiPulse.Services.Ai.Summaries.ItemSummaryService>.Instance);
        return new(llm, Options.Create(options), items, store,
            n => SummaryFixtures.Term("RAG", "Fetch relevant documents from your own data."),
            NullLogger<AiPulse.Services.Ai.Summaries.LlmSummarizer>.Instance, gate);
    }

    [Fact]
    public void Summaries_and_explanations_can_be_switched_independently()
    {
        var gate = new AiSettingsService(null);
        var summarizer = Summarizer(new FakeLlm("x"), gate);
        Assert.True(summarizer.SummariesEnabled && summarizer.ExplanationsEnabled && summarizer.Enabled);

        gate.Update(s => s with { Summaries = false });
        Assert.False(summarizer.SummariesEnabled);
        Assert.True(summarizer.ExplanationsEnabled);
        Assert.True(summarizer.Enabled);

        gate.Update(s => s with { Explanations = false });
        Assert.False(summarizer.Enabled);
    }

    [Fact]
    public async Task With_the_master_switch_off_neither_feature_calls_a_model()
    {
        var gate = new AiSettingsService(null);
        var llm = new FakeLlm("x");
        var summarizer = Summarizer(llm, gate);
        gate.Update(s => s with { Enabled = false });

        var item = await summarizer.SummarizeItemAsync(SummaryFixtures.Item(), "u");

        Assert.False(summarizer.Enabled);
        Assert.Equal(AiPulse.Services.Ai.Summaries.ItemSummaryStatus.Unavailable, item.Status);
        Assert.Contains("switched off", item.Message);
        Assert.Null(await summarizer.ExplainTermAsync("RAG"));
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task A_feature_switched_off_is_refused_on_the_server_even_if_a_stale_page_still_shows_its_button()
    {
        var gate = new AiSettingsService(null);
        var llm = new FakeLlm("Acme released Orbit 2.0 for $20 per month.");
        var summarizer = Summarizer(llm, gate);
        gate.Update(s => s with { Summaries = false });

        var result = await summarizer.SummarizeItemAsync(SummaryFixtures.Item(), "u");

        Assert.False(result.Ok);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public void A_provider_switched_off_by_an_admin_disables_the_features_that_depend_on_it()
    {
        var gate = new AiSettingsService(null);
        var summarizer = Summarizer(new FakeLlm("x"), gate);

        gate.SetProviderEnabled("p", false);

        Assert.False(summarizer.Enabled);
    }
}
