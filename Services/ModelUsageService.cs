using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Tracks which Ollama models the user has pulled or chatted with, so the Explore page can offer a
/// "previously used" quick-pull shortcut even after a model's been deleted locally. Backed by the same
/// SQLite DB as Sources; since the app uses EnsureCreated (not full migrations), this table is added to
/// already-existing databases via a one-time raw-SQL check rather than relying on EnsureCreated (which
/// only creates the whole schema for a brand-new DB file, not incremental tables for existing ones).
/// </summary>
public sealed class ModelUsageService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ModelUsageService(IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task RecordUsageAsync(string slug)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var existing = await db.ModelUsage.FirstOrDefaultAsync(m => m.Slug == slug);
        if (existing is null)
            db.ModelUsage.Add(new ModelUsageRecord { Slug = slug, LastUsedAt = DateTimeOffset.Now });
        else
            existing.LastUsedAt = DateTimeOffset.Now;

        await db.SaveChangesAsync();
    }

    public async Task<List<ModelUsageRecord>> GetRecentAsync(int take = 10)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        // SQLite's EF provider can't translate ORDER BY on DateTimeOffset - order client-side instead
        // (this table stays small, so pulling it all into memory first is cheap).
        var all = await db.ModelUsage.ToListAsync();
        return all.OrderByDescending(m => m.LastUsedAt).Take(take).ToList();
    }

    private async Task EnsureSchemaAsync(AiPulseDbContext db)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaEnsured) return;

            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ModelUsage'";
                var exists = await check.ExecuteScalarAsync() is not null;
                if (!exists)
                {
                    await using var create = conn.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE "ModelUsage" (
                            "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelUsage" PRIMARY KEY AUTOINCREMENT,
                            "Slug" TEXT NOT NULL,
                            "LastUsedAt" TEXT NOT NULL
                        )
                        """;
                    await create.ExecuteNonQueryAsync();
                }
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
