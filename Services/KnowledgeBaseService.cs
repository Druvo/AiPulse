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

    private readonly object _freeApisLock = new();
    private List<FreeApiRecord> _freeApiRecords = new();

    /// <summary>DB-backed (like Sources) so discovery-queue approvals and staleness flags persist - Data/free-apis.json only seeds the table once, on first run.</summary>
    public IReadOnlyList<FreeApiEntry> FreeApis
    {
        get { lock (_freeApisLock) return _freeApiRecords.Select(r => r.ToEntry()).ToList(); }
    }

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
    /// Same problem as <see cref="EnsureSourceColumnsAsync"/> but for whole new tables instead of new
    /// columns - EnsureCreatedAsync only builds tables for a brand-new (empty) database; an install with an
    /// existing App_Data/aipulse.db won't gain FreeApis/FreeApiCandidates automatically just because they're
    /// new DbSets. Checked against sqlite_master and only created if actually missing, so this is a no-op
    /// (and safe to call every startup) on both a genuinely fresh DB, where EnsureCreatedAsync already made
    /// them, and a repeat run where this method already made them.
    /// </summary>
    private static async Task EnsureFreeApiTablesAsync(AiPulseDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('FreeApis','FreeApiCandidates')";
            await using var reader = await check.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(0));
        }

        if (!existing.Contains("FreeApis"))
        {
            await using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE "FreeApis" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FreeApis" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Category" TEXT NOT NULL,
                    "FreeTierSummary" TEXT NOT NULL,
                    "Models" TEXT NOT NULL,
                    "RateLimit" TEXT NOT NULL DEFAULT '',
                    "SignupUrl" TEXT NOT NULL,
                    "HowToUse" TEXT NOT NULL DEFAULT '',
                    "OpenAiCompatible" INTEGER NOT NULL DEFAULT 0,
                    "DocsUrl" TEXT NULL,
                    "LastVerified" TEXT NOT NULL,
                    "NeedsReview" INTEGER NOT NULL DEFAULT 0,
                    "NeedsReviewReason" TEXT NULL
                )
                """;
            await create.ExecuteNonQueryAsync();
        }

        if (!existing.Contains("FreeApiCandidates"))
        {
            await using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE "FreeApiCandidates" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_FreeApiCandidates" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "SourceUrl" TEXT NOT NULL,
                    "RawNote" TEXT NULL,
                    "DiscoveredAt" TEXT NOT NULL,
                    "Status" TEXT NOT NULL DEFAULT 'Pending',
                    "ReviewedAt" TEXT NULL
                )
                """;
            await create.ExecuteNonQueryAsync();
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

    /// <summary>Call once at startup: ensures the FreeApis/FreeApiCandidates tables exist and seeds FreeApis from Data/free-apis.json if empty.</summary>
    public async Task InitializeFreeApisAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await EnsureFreeApiTablesAsync(db);

        if (!await db.FreeApis.AnyAsync())
        {
            var seed = Load<FreeApiEntry>("free-apis.json");
            db.FreeApis.AddRange(seed.Select(a => new FreeApiRecord
            {
                Name = a.Name,
                Category = a.Category,
                FreeTierSummary = a.FreeTierSummary,
                Models = a.Models,
                RateLimit = a.RateLimit,
                SignupUrl = a.SignupUrl,
                HowToUse = a.HowToUse,
                OpenAiCompatible = a.OpenAiCompatible,
                DocsUrl = a.DocsUrl,
                LastVerified = a.LastVerified
            }));
            await db.SaveChangesAsync();
        }

        await RefreshFreeApisAsync(db);
    }

    private async Task RefreshFreeApisAsync(AiPulseDbContext? existing = null)
    {
        var db = existing ?? await _dbFactory.CreateDbContextAsync();
        try
        {
            var list = await db.FreeApis.OrderBy(a => a.Name).ToListAsync();
            lock (_freeApisLock) _freeApiRecords = list;
        }
        finally
        {
            if (existing is null) await db.DisposeAsync();
        }
    }

    public async Task AddFreeApiAsync(FreeApiRecord record)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.FreeApis.Add(record);
        await db.SaveChangesAsync();
        await RefreshFreeApisAsync();
    }

    public async Task UpdateFreeApiAsync(FreeApiRecord record)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.FreeApis.Update(record);
        await db.SaveChangesAsync();
        await RefreshFreeApisAsync();
    }

    public async Task DeleteFreeApiAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.FreeApis.FindAsync(id);
        if (entity is not null)
        {
            db.FreeApis.Remove(entity);
            await db.SaveChangesAsync();
        }
        await RefreshFreeApisAsync();
    }

    /// <summary>Sets or clears the staleness flag on an existing entry - called by FreeApiDiscoveryService's link-check pass.</summary>
    public async Task SetFreeApiReviewFlagAsync(int id, bool needsReview, string? reason)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.FreeApis.FindAsync(id);
        if (entity is null) return;
        entity.NeedsReview = needsReview;
        entity.NeedsReviewReason = reason;
        await db.SaveChangesAsync();
        await RefreshFreeApisAsync();
    }

    public async Task<List<FreeApiCandidate>> GetPendingCandidatesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.FreeApiCandidates.Where(c => c.Status == "Pending").OrderByDescending(c => c.DiscoveredAt).ToListAsync();
    }

    /// <summary>
    /// Adds a discovery candidate unless it overlaps (case-insensitive, either-direction substring so
    /// "Groq" and "Groq (GroqCloud)" count as the same provider) an existing live entry or an already-pending
    /// candidate. Returns false when skipped as a likely duplicate.
    /// </summary>
    public async Task<bool> AddCandidateIfNewAsync(string name, string sourceUrl, string? rawNote)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingNames = await db.FreeApis.Select(a => a.Name).ToListAsync();
        var pendingNames = await db.FreeApiCandidates.Where(c => c.Status == "Pending").Select(c => c.Name).ToListAsync();

        bool Overlaps(string a, string b) =>
            a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

        if (existingNames.Any(n => Overlaps(n, name)) || pendingNames.Any(n => Overlaps(n, name)))
            return false;

        db.FreeApiCandidates.Add(new FreeApiCandidate { Name = name, SourceUrl = sourceUrl, RawNote = rawNote });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task DismissCandidateAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var c = await db.FreeApiCandidates.FindAsync(id);
        if (c is null) return;
        c.Status = "Dismissed";
        c.ReviewedAt = DateTimeOffset.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>Approving adds the new record (already filled in by the admin's review form) and marks the candidate resolved - never auto-inserted with guessed fields.</summary>
    public async Task ApproveCandidateAsync(int candidateId, FreeApiRecord record)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.FreeApis.Add(record);
        var c = await db.FreeApiCandidates.FindAsync(candidateId);
        if (c is not null)
        {
            c.Status = "Approved";
            c.ReviewedAt = DateTimeOffset.Now;
        }
        await db.SaveChangesAsync();
        await RefreshFreeApisAsync();
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
