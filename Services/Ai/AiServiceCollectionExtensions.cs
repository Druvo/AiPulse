using AiPulse.Models;
using AiPulse.Services.Ai.Agent;
using AiPulse.Services.Ai.Llm;
using AiPulse.Services.Ai.Rag;
using AiPulse.Services.Ai.Summaries;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>Registers the grounded question-answering stack: language-model providers, the knowledge index and the agent.</summary>
    public static IServiceCollection AddAiAssistant(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection(AiOptions.Section));
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));

        // Each provider carries its own timeout (LlmClient cancels by token), so the client itself must not cut a slow local model off at 100s.
        services.AddHttpClient("llm", c => c.Timeout = Timeout.InfiniteTimeSpan);

        // LlmClient has a second constructor that lets tests inject providers, which the container cannot choose between.
        services.AddSingleton<ILlmClient>(sp => new LlmClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<LlmClient>>()));
        services.AddSingleton<AskRateLimiter>();

        services.AddSingleton(sp =>
        {
            var kb = sp.GetRequiredService<KnowledgeBaseService>();
            var history = sp.GetRequiredService<FeedHistoryService>();
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeCorpus>();

            return new KnowledgeCorpus(
                () => KnowledgeCorpusBuilder.Build(new KnowledgeInputs(
                    kb.Glossary, kb.Tools, kb.Practices, kb.Learning, kb.Benchmarks, kb.Courses, kb.ModelDirectory,
                    history.Items.Take(Math.Max(0, options.NewsDocLimit)))),
                TimeSpan.FromMinutes(Math.Max(1, options.CorpusRefreshMinutes)),
                ex => log.LogWarning(ex, "Knowledge index refresh failed; still answering from the previous index"));
        });

        services.AddSingleton<RagAgent>();

        // Saved summaries live beside the other App_Data files, so a backup/restore carries them along.
        services.AddSingleton(sp => new SummaryStore(Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "summaries.jsonl")));
        services.AddSingleton<ItemSummaryService>();
        services.AddSingleton<ISummarizer>(sp =>
        {
            var kb = sp.GetRequiredService<KnowledgeBaseService>();
            return new LlmSummarizer(
                sp.GetRequiredService<ILlmClient>(),
                sp.GetRequiredService<IOptions<LlmOptions>>(),
                sp.GetRequiredService<ItemSummaryService>(),
                sp.GetRequiredService<SummaryStore>(),
                name => kb.Glossary.FirstOrDefault(t => string.Equals(t.Term, name, StringComparison.OrdinalIgnoreCase)),
                sp.GetRequiredService<ILogger<LlmSummarizer>>());
        });

        return services;
    }
}
