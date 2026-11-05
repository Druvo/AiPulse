using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AiPulse.Services.Productivity;

/// <summary>
/// The productivity features' own tables (saved searches + alert dedupe, bookmark collections/notes, digest and onboarding settings),
/// in the same App_Data/aipulse.db as everything else. A separate DbContext (rather than more DbSets on AiPulseDbContext) keeps these
/// features self-contained; the schema is bootstrapped by hand with CREATE TABLE IF NOT EXISTS, the same "EnsureCreated does not add
/// tables to an existing database" reasoning as UserService/FollowService, so an existing install simply gains the tables on first use.
/// </summary>
public sealed class ProductivityDbContext : DbContext
{
    public ProductivityDbContext(DbContextOptions<ProductivityDbContext> options) : base(options) { }

    public DbSet<SavedSearchRule> SavedSearchRules => Set<SavedSearchRule>();
    public DbSet<SavedSearchHit> SavedSearchHits => Set<SavedSearchHit>();
    public DbSet<BookmarkCollection> BookmarkCollections => Set<BookmarkCollection>();
    public DbSet<BookmarkCollectionEntry> BookmarkCollectionEntries => Set<BookmarkCollectionEntry>();
    public DbSet<BookmarkAnnotation> BookmarkAnnotations => Set<BookmarkAnnotation>();
    public DbSet<UserProductivitySettings> UserProductivitySettings => Set<UserProductivitySettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SavedSearchRule>().ToTable("SavedSearchRules");
        b.Entity<SavedSearchHit>().ToTable("SavedSearchHits");
        b.Entity<BookmarkCollection>().ToTable("BookmarkCollections");
        b.Entity<BookmarkCollectionEntry>().ToTable("BookmarkCollectionEntries");
        b.Entity<BookmarkAnnotation>().ToTable("BookmarkAnnotations");
        b.Entity<UserProductivitySettings>().ToTable("UserProductivitySettings").HasKey(s => s.UserId);
        b.Entity<UserProductivitySettings>().Property(s => s.UserId).ValueGeneratedNever();

        // SQLite stores DateTime as text with no kind; everything here is written as UTC, so say so on the way back out.
        var utc = new ValueConverter<DateTime, DateTime>(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? null : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
            v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
            else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
        }
    }

    private static readonly string[] Schema =
    {
        """
        CREATE TABLE IF NOT EXISTS "SavedSearchRules" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SavedSearchRules" PRIMARY KEY AUTOINCREMENT,
            "UserId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Keywords" TEXT NOT NULL,
            "Tags" TEXT NOT NULL,
            "Sources" TEXT NOT NULL,
            "AlertEnabled" INTEGER NOT NULL,
            "WebhookEnabled" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL,
            "AlertsSinceUtc" TEXT NULL,
            "LastAlertUtc" TEXT NULL
        )
        """,
        """CREATE INDEX IF NOT EXISTS "IX_SavedSearchRules_UserId" ON "SavedSearchRules" ("UserId")""",
        """
        CREATE TABLE IF NOT EXISTS "SavedSearchHits" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SavedSearchHits" PRIMARY KEY AUTOINCREMENT,
            "RuleId" INTEGER NOT NULL,
            "UserId" INTEGER NOT NULL,
            "LinkHash" TEXT NOT NULL,
            "Link" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "SourceName" TEXT NOT NULL,
            "Notified" INTEGER NOT NULL,
            "CreatedUtc" TEXT NOT NULL
        )
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SavedSearchHits_Rule_Link" ON "SavedSearchHits" ("RuleId", "LinkHash")""",
        """CREATE INDEX IF NOT EXISTS "IX_SavedSearchHits_User_Created" ON "SavedSearchHits" ("UserId", "CreatedUtc")""",
        """
        CREATE TABLE IF NOT EXISTS "BookmarkCollections" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_BookmarkCollections" PRIMARY KEY AUTOINCREMENT,
            "UserId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "CreatedUtc" TEXT NOT NULL
        )
        """,
        """CREATE INDEX IF NOT EXISTS "IX_BookmarkCollections_UserId" ON "BookmarkCollections" ("UserId")""",
        """
        CREATE TABLE IF NOT EXISTS "BookmarkCollectionEntries" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_BookmarkCollectionEntries" PRIMARY KEY AUTOINCREMENT,
            "CollectionId" INTEGER NOT NULL,
            "UserId" INTEGER NOT NULL,
            "Link" TEXT NOT NULL,
            "AddedUtc" TEXT NOT NULL
        )
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookmarkCollectionEntries_Collection_Link" ON "BookmarkCollectionEntries" ("CollectionId", "Link")""",
        """CREATE INDEX IF NOT EXISTS "IX_BookmarkCollectionEntries_UserId" ON "BookmarkCollectionEntries" ("UserId")""",
        """
        CREATE TABLE IF NOT EXISTS "BookmarkAnnotations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_BookmarkAnnotations" PRIMARY KEY AUTOINCREMENT,
            "UserId" INTEGER NOT NULL,
            "Link" TEXT NOT NULL,
            "Tags" TEXT NOT NULL,
            "Note" TEXT NOT NULL,
            "UpdatedUtc" TEXT NOT NULL
        )
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookmarkAnnotations_User_Link" ON "BookmarkAnnotations" ("UserId", "Link")""",
        """
        CREATE TABLE IF NOT EXISTS "UserProductivitySettings" (
            "UserId" INTEGER NOT NULL CONSTRAINT "PK_UserProductivitySettings" PRIMARY KEY,
            "DigestOptIn" INTEGER NOT NULL,
            "DigestLastSentUtc" TEXT NULL,
            "DigestNonce" TEXT NOT NULL,
            "OnboardingDismissed" INTEGER NOT NULL,
            "OnboardingManualSteps" TEXT NOT NULL
        )
        """,
    };

    /// <summary>Creates any missing table/index. Idempotent; callers go through <see cref="ProductivityDb"/>, which runs it once per process.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        foreach (var sql in Schema)
            await Database.ExecuteSqlRawAsync(sql, ct);
    }
}

/// <summary>Hands out contexts with the schema guaranteed to exist. Singleton; the bootstrap runs once per process.</summary>
public sealed class ProductivityDb
{
    private readonly IDbContextFactory<ProductivityDbContext> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    public ProductivityDb(IDbContextFactory<ProductivityDbContext> factory) => _factory = factory;

    public async Task<ProductivityDbContext> OpenAsync(CancellationToken ct = default)
    {
        var db = await _factory.CreateDbContextAsync(ct);
        if (_ready) return db;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_ready)
            {
                await db.EnsureSchemaAsync(ct);
                _ready = true;
            }
        }
        finally { _gate.Release(); }
        return db;
    }
}
