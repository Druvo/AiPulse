using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Loads the curated, standalone knowledge base from the Data/*.json files.</summary>
public sealed class KnowledgeBaseService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDir;

    public KnowledgeBaseService(IWebHostEnvironment env)
    {
        _dataDir = Path.Combine(env.ContentRootPath, "Data");
    }

    public IReadOnlyList<GlossaryTerm> Glossary => _glossary ??= Load<GlossaryTerm>("glossary.json");
    public IReadOnlyList<ToolEntry> Tools => _tools ??= Load<ToolEntry>("tools.json");
    public IReadOnlyList<PracticeTip> Practices => _practices ??= Load<PracticeTip>("practices.json");
    public IReadOnlyList<FeedSource> Sources => _sources ??= Load<FeedSource>("sources.json");
    public IReadOnlyList<LearningModule> Learning => _learning ??= Load<LearningModule>("learning.json");

    private List<GlossaryTerm>? _glossary;
    private List<ToolEntry>? _tools;
    private List<PracticeTip>? _practices;
    private List<FeedSource>? _sources;
    private List<LearningModule>? _learning;

    private List<T> Load<T>(string file)
    {
        var path = Path.Combine(_dataDir, file);
        if (!File.Exists(path))
            return new List<T>();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new List<T>();
    }
}
