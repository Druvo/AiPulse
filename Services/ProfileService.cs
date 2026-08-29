using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Public-facing profile fields (bio/avatar/website/display name) for a user's /u/{username} page. Same
/// EnsureCreated caveat as other DB-backed services here - the UserProfiles table is reconciled with a
/// one-time raw-SQL check on first use (same pattern as UserService.EnsureSchemaAsync).
///
/// Kept as its own table, 1:1 with AppUser.Id, rather than more fields on ReadingState's private per-user
/// JSON - see UserProfileRecord's doc comment for why (a stranger's browser shouldn't need to open this
/// user's private bookmarks/watchlist/webhook file just to read three public strings, and keying by the
/// stable AppUser.Id means a rename touches zero rows here).
/// </summary>
public sealed class ProfileService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ProfileService(IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Returns an empty (never-saved) profile if the user hasn't set one up yet - callers don't need to null-check.</summary>
    public async Task<UserProfileRecord> GetProfileAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.UserProfiles.FindAsync(userId) ?? new UserProfileRecord { UserId = userId };
    }

    public async Task UpsertProfileAsync(int userId, string? bio, string? avatarUrl, string? websiteUrl, string? displayName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var profile = await db.UserProfiles.FindAsync(userId);
        if (profile is null)
        {
            profile = new UserProfileRecord { UserId = userId };
            db.UserProfiles.Add(profile);
        }

        profile.Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        profile.AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        profile.WebsiteUrl = string.IsNullOrWhiteSpace(websiteUrl) ? null : websiteUrl.Trim();
        profile.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        profile.UpdatedAt = DateTimeOffset.Now;

        await db.SaveChangesAsync();
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
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='UserProfiles'";
            var exists = await check.ExecuteScalarAsync() is not null;

            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "UserProfiles" (
                        "UserId" INTEGER NOT NULL CONSTRAINT "PK_UserProfiles" PRIMARY KEY,
                        "Bio" TEXT NULL,
                        "AvatarUrl" TEXT NULL,
                        "WebsiteUrl" TEXT NULL,
                        "DisplayName" TEXT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
