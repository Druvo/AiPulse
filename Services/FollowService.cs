using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Which sources/tags each user follows, backing the News page's "Following" filter and public
/// follower-count aggregates on profile pages. Same EnsureCreated caveat as other DB-backed services here -
/// existing databases don't automatically gain new tables, so the Follows table is reconciled with a
/// one-time raw-SQL check on first use (same pattern as UserService.EnsureSchemaAsync).
///
/// Every method takes an explicit int userId rather than resolving "the current user" ambiently - unlike
/// ReadingStateService, which used to fall back to a shared "anonymous" identity when there wasn't one
/// (see its EnsureLoaded fix). Requiring the caller to already have a real user id makes "this needs a
/// signed-in user" visible at every call site instead of a runtime fallback.
/// </summary>
public sealed class FollowService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public FollowService(IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<bool> IsFollowingAsync(int userId, string followType, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.Follows.AnyAsync(f => f.UserId == userId && f.FollowType == followType && f.FollowKey == key);
    }

    public Task<bool> IsFollowingSourceAsync(int userId, string sourceName) => IsFollowingAsync(userId, FollowTypes.Source, sourceName);
    public Task<bool> IsFollowingTagAsync(int userId, string tag) => IsFollowingAsync(userId, FollowTypes.Tag, tag.ToLowerInvariant());

    /// <summary>Toggles the follow. Returns the new following state.</summary>
    public async Task<bool> ToggleFollowAsync(int userId, string followType, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var existing = await db.Follows.FirstOrDefaultAsync(f => f.UserId == userId && f.FollowType == followType && f.FollowKey == key);
        if (existing is not null)
        {
            db.Follows.Remove(existing);
            await db.SaveChangesAsync();
            return false;
        }

        db.Follows.Add(new FollowRecord { UserId = userId, FollowType = followType, FollowKey = key });
        await db.SaveChangesAsync();
        return true;
    }

    public Task<bool> ToggleFollowSourceAsync(int userId, string sourceName) => ToggleFollowAsync(userId, FollowTypes.Source, sourceName);
    public Task<bool> ToggleFollowTagAsync(int userId, string tag) => ToggleFollowAsync(userId, FollowTypes.Tag, tag.ToLowerInvariant());

    public async Task<HashSet<string>> GetFollowedSourcesAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        var keys = await db.Follows.Where(f => f.UserId == userId && f.FollowType == FollowTypes.Source).Select(f => f.FollowKey).ToListAsync();
        return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HashSet<string>> GetFollowedTagsAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        var keys = await db.Follows.Where(f => f.UserId == userId && f.FollowType == FollowTypes.Tag).Select(f => f.FollowKey).ToListAsync();
        return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Public aggregate for profile/source pages - "N followers of this tag/source".</summary>
    public async Task<int> GetFollowerCountAsync(string followType, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.Follows.CountAsync(f => f.FollowType == followType && f.FollowKey == key);
    }

    /// <summary>Public aggregate for profile pages - "following N things".</summary>
    public async Task<int> GetFollowingCountAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.Follows.CountAsync(f => f.UserId == userId);
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

            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Follows'";
            var exists = await check.ExecuteScalarAsync() is not null;

            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "Follows" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Follows" PRIMARY KEY AUTOINCREMENT,
                        "UserId" INTEGER NOT NULL,
                        "FollowType" TEXT NOT NULL,
                        "FollowKey" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                await using var uniqueIndex = conn.CreateCommand();
                uniqueIndex.CommandText = """CREATE UNIQUE INDEX "IX_Follows_User_Type_Key" ON "Follows" ("UserId", "FollowType", "FollowKey")""";
                await uniqueIndex.ExecuteNonQueryAsync();

                await using var typeKeyIndex = conn.CreateCommand();
                typeKeyIndex.CommandText = """CREATE INDEX "IX_Follows_Type_Key" ON "Follows" ("FollowType", "FollowKey")""";
                await typeKeyIndex.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
