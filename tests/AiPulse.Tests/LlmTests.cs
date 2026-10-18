using System.Net;
using System.Text.Json;
using AiPulse.Services.Ai.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiPulse.Tests;

public class LlmClientTests
{
    private static readonly LlmRequest Ask = new(new[] { LlmMessage.User("hi") });

    private static LlmClient Client(params (StubProvider Provider, double TimeoutSeconds)[] providers) =>
        new(providers.Select(p => ((ILlmProvider)p.Provider, TimeSpan.FromSeconds(p.TimeoutSeconds))), NullLogger<LlmClient>.Instance);

    [Fact]
    public async Task Providers_are_tried_in_priority_order_and_the_first_answer_wins()
    {
        var late = StubProvider.Ok("late", 50, "from late");
        var early = StubProvider.Ok("early", 10, "from early");

        var response = await Client((late, 5), (early, 5)).ChatAsync(Ask);

        Assert.Equal("from early", response.Text);
        Assert.Equal(1, early.Calls);
        Assert.Equal(0, late.Calls);
    }

    [Fact]
    public async Task A_failing_provider_falls_through_to_the_next()
    {
        var broken = StubProvider.Failing("broken", 1, "connection refused");
        var backup = StubProvider.Ok("backup", 2, "from backup");

        var response = await Client((broken, 5), (backup, 5)).ChatAsync(Ask);

        Assert.Equal("backup", response.Provider);
        Assert.Equal(1, broken.Calls);
    }

    [Fact]
    public async Task When_every_provider_fails_the_exception_lists_each_attempt()
    {
        var client = Client((StubProvider.Failing("a", 1, "refused"), 5), (StubProvider.Failing("b", 2, "401 unauthorized"), 5));

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));

        Assert.Equal(new[] { "a", "b" }, ex.Attempts.Select(a => a.Provider));
        Assert.Contains("refused", ex.Message);
        Assert.Contains("401 unauthorized", ex.Message);
    }

    [Fact]
    public async Task A_provider_that_hangs_is_cut_off_at_its_timeout_and_the_next_is_tried()
    {
        var hung = new StubProvider("hung", 1, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var backup = StubProvider.Ok("backup", 2, "ok");

        var response = await Client((hung, 0.15), (backup, 5)).ChatAsync(Ask);

        Assert.Equal("backup", response.Provider);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_one()
    {
        var hung = new StubProvider("hung", 1, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => Client((hung, 0.1)).ChatAsync(Ask));

        Assert.Contains("timed out", Assert.Single(ex.Attempts).Error);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_as_a_provider_failure()
    {
        var backup = StubProvider.Ok("backup", 2, "ok");
        var slow = new StubProvider("slow", 1, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client((slow, 30), (backup, 5)).ChatAsync(Ask, cts.Token));

        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task With_no_providers_it_reports_unavailable_rather_than_hanging()
    {
        var client = Client();

        Assert.False(client.HasProviders);
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => client.ChatAsync(Ask));
        Assert.Empty(ex.Attempts);
    }

    [Fact]
    public void Provider_info_never_exposes_an_api_key()
    {
        var info = Client((StubProvider.Ok("p", 1, "x"), 5)).Providers.Single();
        Assert.Equal("p", info.Name);
        Assert.DoesNotContain(typeof(LlmProviderInfo).GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }
}

public class ProviderConfigurationTests
{
    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static LlmClient Build(LlmOptions options, IConfiguration? config = null, HttpMessageHandler? handler = null) =>
        new(new Factory(handler ?? new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            Options.Create(options), config ?? Config(), NullLogger<LlmClient>.Instance);

    [Fact]
    public void With_nothing_configured_the_existing_Ollama_setting_becomes_the_single_provider()
    {
        var client = Build(new LlmOptions(), Config(("Ollama:BaseUrl", "http://192.168.0.100:11434")));

        var provider = Assert.Single(client.Providers);
        Assert.Equal("ollama", provider.Name);
        Assert.Equal("ollama", provider.Kind);
    }

    [Fact]
    public void Configured_providers_are_ordered_by_priority_and_disabled_ones_are_left_out()
    {
        var client = Build(new LlmOptions
        {
            Providers =
            {
                new LlmProviderOptions { Name = "hosted", Kind = "openai", BaseUrl = "https://api.example.com/v1", Model = "m", Priority = 20 },
                new LlmProviderOptions { Name = "local", Kind = "ollama", BaseUrl = "http://localhost:11434", Priority = 10 },
                new LlmProviderOptions { Name = "off", Kind = "openai", BaseUrl = "https://x", Model = "m", Priority = 1, Enabled = false },
            }
        });

        Assert.Equal(new[] { "local", "hosted" }, client.Providers.Select(p => p.Name));
    }

    [Fact]
    public void An_unknown_kind_is_ignored_instead_of_breaking_startup()
    {
        var client = Build(new LlmOptions { Providers = { new LlmProviderOptions { Name = "x", Kind = "carrier-pigeon", BaseUrl = "http://x" } } });
        Assert.False(client.HasProviders);
    }

    [Fact]
    public async Task A_configured_openai_provider_is_reachable_through_the_client()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"model":"gpt-x","choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}"""));
        var client = Build(new LlmOptions
        {
            Providers = { new LlmProviderOptions { Name = "hosted", Kind = "OpenAI", BaseUrl = "https://api.example.com/v1/", Model = "gpt-x", ApiKey = "sk-test-123" } }
        }, handler: handler);

        var response = await client.ChatAsync(new LlmRequest(new[] { LlmMessage.User("hi") }));

        Assert.Equal("hello", response.Text);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.Seen.Single().Request.RequestUri!.ToString());
    }
}

public class OllamaLlmProviderTests
{
    private static OllamaLlmProvider Provider(StubHttpHandler handler, string model = "qwen:7b") =>
        new(new HttpClient(handler), new LlmProviderOptions { Name = "local", BaseUrl = "http://ollama:11434/", Model = model });

    private static readonly LlmRequest Req = new(new[] { LlmMessage.System("be brief"), LlmMessage.User("hi") }, 0.2, 123);

    [Fact]
    public async Task Sends_a_non_streaming_chat_and_reads_text_and_token_counts()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"message":{"role":"assistant","content":"  Hello there  "},"prompt_eval_count":11,"eval_count":4}"""));

        var response = await Provider(handler).ChatAsync(Req, CancellationToken.None);

        Assert.Equal("Hello there", response.Text);
        Assert.Equal((11, 4), (response.PromptTokens, response.CompletionTokens));
        Assert.Equal("qwen:7b", response.Model);

        var (request, body) = handler.Seen.Single();
        Assert.Equal("http://ollama:11434/api/chat", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, json.RootElement.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(123, json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Without_a_configured_model_it_uses_the_first_installed_one_and_remembers_it()
    {
        var handler = new StubHttpHandler(r => r.RequestUri!.AbsolutePath == "/api/tags"
            ? StubHttpHandler.JsonResponse("""{"models":[{"name":"llama3:8b"},{"name":"other"}]}""")
            : StubHttpHandler.JsonResponse("""{"message":{"content":"ok"}}"""));
        var provider = Provider(handler, model: "");

        var first = await provider.ChatAsync(Req, CancellationToken.None);
        await provider.ChatAsync(Req, CancellationToken.None);

        Assert.Equal("llama3:8b", first.Model);
        Assert.Equal(1, handler.Seen.Count(s => s.Request.RequestUri!.AbsolutePath == "/api/tags"));
    }

    [Fact]
    public async Task No_installed_models_gives_an_actionable_error()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"models":[]}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler, "").ChatAsync(Req, CancellationToken.None));
        Assert.Contains("pull one", ex.Message);
    }

    [Fact]
    public async Task A_server_error_surfaces_the_status()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(handler).ChatAsync(Req, CancellationToken.None));
        Assert.Contains("404", ex.Message);
        Assert.Contains("model not found", ex.Message);
    }

    [Fact]
    public async Task An_empty_reply_is_an_error_not_an_empty_answer()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"message":{"content":"   "}}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).ChatAsync(Req, CancellationToken.None));
    }
}

public class OpenAiCompatibleProviderTests
{
    private static readonly LlmRequest Req = new(new[] { LlmMessage.User("hi") }, 0.1, 50);

    private static OpenAiCompatibleProvider Provider(StubHttpHandler handler, string? key = "sk-secret-value-123", string model = "gpt-x") =>
        new(new HttpClient(handler), new LlmProviderOptions { Name = "hosted", Kind = "openai", BaseUrl = "https://api.example.com/v1", Model = model, ApiKey = key });

    [Fact]
    public async Task Posts_to_chat_completions_with_a_bearer_key_and_parses_the_reply()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"model":"gpt-x-2026","choices":[{"message":{"content":" Hi! "}}],"usage":{"prompt_tokens":8,"completion_tokens":2}}"""));

        var response = await Provider(handler).ChatAsync(Req, CancellationToken.None);

        Assert.Equal("Hi!", response.Text);
        Assert.Equal("gpt-x-2026", response.Model);
        Assert.Equal((8, 2), (response.PromptTokens, response.CompletionTokens));

        var (request, body) = handler.Seen.Single();
        Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret-value-123", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("gpt-x", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(50, json.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Without_a_key_no_authorization_header_is_sent()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}"""));
        await Provider(handler, key: null).ChatAsync(Req, CancellationToken.None);
        Assert.Null(handler.Seen.Single().Request.Headers.Authorization);
    }

    [Fact]
    public async Task A_rejected_request_reports_the_status_and_never_echoes_the_key()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"error":{"message":"invalid api key"}}""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(handler).ChatAsync(Req, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("sk-secret-value-123", ex.Message);
    }

    [Fact]
    public async Task Reasoning_effort_is_sent_only_when_configured()
    {
        var reply = """{"choices":[{"message":{"content":"ok"}}]}""";

        var without = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(reply));
        await Provider(without).ChatAsync(Req, CancellationToken.None);
        Assert.False(JsonDocument.Parse(without.Seen.Single().Body).RootElement.TryGetProperty("reasoning_effort", out _));

        var with = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(reply));
        var provider = new OpenAiCompatibleProvider(new HttpClient(with),
            new LlmProviderOptions { Name = "g", BaseUrl = "https://x/v1", Model = "m", ReasoningEffort = "low" });
        await provider.ChatAsync(Req, CancellationToken.None);
        Assert.Equal("low", JsonDocument.Parse(with.Seen.Single().Body).RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task An_empty_reply_cut_off_by_the_token_limit_says_so()
    {
        // What Gemini really returned: HTTP 200, no text, finish_reason "length" - the hidden reasoning ate the budget.
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"choices":[{"finish_reason":"length","message":{"role":"assistant"}}],"usage":{"completion_tokens":0}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).ChatAsync(Req, CancellationToken.None));

        Assert.Contains("token limit", ex.Message);
        Assert.Contains("ReasoningEffort", ex.Message);
    }

    [Fact]
    public async Task A_model_is_required()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler, model: "").ChatAsync(Req, CancellationToken.None));
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task A_reply_with_no_choices_is_an_error()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse("""{"choices":[]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).ChatAsync(Req, CancellationToken.None));
    }
}
