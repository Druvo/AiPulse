using System.Text.Json;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Loads the curated, standalone knowledge base from the Data/*.json files, plus the dynamically
/// managed Sources list, which lives in SQLite (App_Data/aipulse.db) so it can be added/edited/removed
/// from the Sources page without restarting. Data/sources.json is only read once, to seed the DB on
/// first run - after that the DB is the source of truth.
/// </summary>
public sealed class KnowledgeBaseService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDir;
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly object _sourcesLock = new();
    private List<SourceRecord> _sourceRecords = new();

    public KnowledgeBaseService(IWebHostEnvironment env, IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dataDir = Path.Combine(env.ContentRootPath, "Data");
        _dbFactory = dbFactory;
    }

    public IReadOnlyList<GlossaryTerm> Glossary => _glossary ??= Load<GlossaryTerm>("glossary.json");
    public IReadOnlyList<ToolEntry> Tools => _tools ??= Load<ToolEntry>("tools.json");
    public IReadOnlyList<PracticeTip> Practices => _practices ??= Load<PracticeTip>("practices.json");
    public IReadOnlyList<LearningModule> Learning => _learning ??= Load<LearningModule>("learning.json");
    public IReadOnlyList<BenchmarkEntry> Benchmarks => _benchmarks ??= Load<BenchmarkEntry>("benchmarks.json");
    public IReadOnlyList<ModelDirectoryEntry> ModelDirectory => _modelDirectory ??= Load<ModelDirectoryEntry>("model-directory.json");
    public IReadOnlyList<CourseEntry> Courses => _courses ??= Load<CourseEntry>("courses.json");

    private List<GlossaryTerm>? _glossary;
    private List<ToolEntry>? _tools;
    private List<PracticeTip>? _practices;
    private List<LearningModule>? _learning;
    private List<BenchmarkEntry>? _benchmarks;
    private List<ModelDirectoryEntry>? _modelDirectory;
    private List<CourseEntry>? _courses;

    /// <summary>Sources mapped to the plain DTO used everywhere else (FeedAggregatorService, News, etc.).</summary>
    public IReadOnlyList<FeedSource> Sources
    {
        get { lock (_sourcesLock) return _sourceRecords.Select(s => s.ToFeedSource()).ToList(); }
    }

    /// <summary>Sources with their DB Id, for the Sources page's edit/delete UI.</summary>
    public IReadOnlyList<SourceRecord> SourceRecords
    {
        get { lock (_sourcesLock) return _sourceRecords.ToList(); }
    }

    /// <summary>Call once at startup: ensures the DB exists and seeds it from Data/sources.json if empty.</summary>
    public async Task InitializeSourcesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await EnsureSourceColumnsAsync(db);

        if (!await db.Sources.AnyAsync())
        {
            var seed = Load<FeedSource>("sources.json");
            db.Sources.AddRange(seed.Select(s => new SourceRecord
            {
                Name = s.Name,
                Url = s.Url,
                Category = s.Category,
                Enabled = s.Enabled,
                Note = s.Note,
                ContentType = s.ContentType,
                Level = s.Level,
                Tags = s.Tags
            }));
            await db.SaveChangesAsync();
        }
        else
        {
            await ReconcileSeedUpdatesAsync(db);
        }

        await RefreshSourcesAsync(db);
    }

    /// <summary>
    /// EnsureCreated only builds the schema for a brand-new DB - an existing Sources table (from before
    /// full-text-fetch/scrape support existed) won't automatically gain the new columns, so add them
    /// directly if missing.
    /// </summary>
    private static async Task EnsureSourceColumnsAsync(AiPulseDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var newColumns = new (string Name, string Def)[]
        {
            ("FullTextFetch", "INTEGER NOT NULL DEFAULT 0"),
            ("IsScrape", "INTEGER NOT NULL DEFAULT 0"),
            ("ScrapeItemXPath", "TEXT NULL"),
            ("ScrapeTitleXPath", "TEXT NULL"),
            ("ScrapeLinkXPath", "TEXT NULL"),
            ("ScrapeDateXPath", "TEXT NULL")
        };

        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Sources)";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        foreach (var (name, def) in newColumns)
        {
            if (existing.Contains(name)) continue;
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"""ALTER TABLE "Sources" ADD COLUMN "{name}" {def}""";
            await alter.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// One-off fixups for installs that already seeded their DB before a source was added/corrected in
    /// Data/sources.json - since the DB is only auto-seeded when empty, these apply the delta directly.
    /// </summary>
    private async Task ReconcileSeedUpdatesAsync(AiPulseDbContext db)
    {
        var existingNames = await db.Sources.Select(s => s.Name).ToListAsync();
        var changed = false;

        if (!existingNames.Contains("OpenAI News", StringComparer.OrdinalIgnoreCase))
        {
            db.Sources.Add(new SourceRecord
            {
                Name = "OpenAI News",
                Url = "https://openai.com/news/rss.xml",
                Category = "News",
                Enabled = true,
                Note = "Official OpenAI announcements and product news.",
                ContentType = "News",
                Level = "Beginner",
                Tags = new[] { "Product Launches" }
            });
            changed = true;
        }

        var googleAi = await db.Sources.FirstOrDefaultAsync(s => s.Name == "Google · AI");
        if (googleAi is not null && googleAi.Url == "https://blog.google/technology/ai/rss/")
        {
            googleAi.Url = "https://blog.google/innovation-and-ai/technology/ai/rss/";
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    public async Task AddSourceAsync(SourceRecord record)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Sources.Add(record);
        await db.SaveChangesAsync();
        await RefreshSourcesAsync();
    }

    public async Task UpdateSourceAsync(SourceRecord record)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Sources.Update(record);
        await db.SaveChangesAsync();
        await RefreshSourcesAsync();
    }

    public async Task DeleteSourceAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(id);
        if (entity is not null)
        {
            db.Sources.Remove(entity);
            await db.SaveChangesAsync();
        }
        await RefreshSourcesAsync();
    }

    public async Task ToggleSourceAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(id);
        if (entity is not null)
        {
            entity.Enabled = !entity.Enabled;
            await db.SaveChangesAsync();
        }
        await RefreshSourcesAsync();
    }

    private async Task RefreshSourcesAsync(AiPulseDbContext? existing = null)
    {
        var db = existing ?? await _dbFactory.CreateDbContextAsync();
        try
        {
            var list = await db.Sources.OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync();
            lock (_sourcesLock) _sourceRecords = list;
        }
        finally
        {
            if (existing is null) await db.DisposeAsync();
        }
    }

    private List<T> Load<T>(string file)
    {
        var path = Path.Combine(_dataDir, file);
        if (!File.Exists(path))
            return new List<T>();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new List<T>();
    }
}
