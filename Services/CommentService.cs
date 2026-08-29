using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

/// <summary>
/// Comments on articles, keyed by ArticleLink (FeedItem.Link - AiPulse has no internal article page of its
/// own, Link always points at the original external site). Same EnsureCreated caveat as other DB-backed
/// services here - the Comments table is reconciled with a one-time raw-SQL check on first use (same
/// pattern as UserService.EnsureSchemaAsync).
///
/// Deliberately no denormalized Username column on CommentRecord (unlike AuditLogEntry, which copies
/// actor/target usernames so the log stays readable after a rename). A comment must show the *current*
/// username, not a rename-time snapshot, so display data is resolved via a join to Users at query time -
/// meaning a rename needs zero additional code here, unlike the existing UserService.RenameUserAsync +
/// ReadingStateService.RenameUserFolder coupling this deliberately avoids repeating a third time.
/// </summary>
public sealed class CommentService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public CommentService(IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<CommentView>> GetForArticleAsync(string articleLink)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        // SQLite has no native DateTimeOffset type, and EF's SQLite provider can't translate ORDER BY over
        // it - order client-side after fetching instead (same reason GetByUserAsync below does the same).
        var comments = await db.Comments.Where(c => c.ArticleLink == articleLink).ToListAsync();
        var ordered = comments.OrderBy(c => c.CreatedAt).ToList();
        return await AttachUsersAsync(db, ordered);
    }

    public async Task<int> GetCommentCountAsync(string articleLink)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return await db.Comments.CountAsync(c => c.ArticleLink == articleLink && c.DeletedAt == null);
    }

    /// <summary>Public activity feed for a user's profile page.</summary>
    public async Task<List<CommentView>> GetByUserAsync(int userId, int take = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var comments = await db.Comments.Where(c => c.UserId == userId && c.DeletedAt == null).ToListAsync();
        var ordered = comments.OrderByDescending(c => c.CreatedAt).Take(take).ToList();
        return await AttachUsersAsync(db, ordered);
    }

    public async Task<CommentView> AddAsync(string articleLink, int userId, string body, int? parentCommentId = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Comment body can't be empty.", nameof(body));

        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var comment = new CommentRecord
        {
            ArticleLink = articleLink,
            UserId = userId,
            ParentCommentId = parentCommentId,
            Body = body.Trim()
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(userId);
        return ToView(comment, user);
    }

    /// <summary>Author-only edit. Returns false (no-op) if the requesting user doesn't own the comment.</summary>
    public async Task<bool> EditAsync(int commentId, int requestingUserId, string newBody)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.UserId != requestingUserId || comment.DeletedAt is not null)
            return false;

        comment.Body = newBody.Trim();
        comment.EditedAt = DateTimeOffset.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Soft-deletes a comment. Allowed for the comment's own author, or an Admin/Curator moderating
    /// someone else's. Returns false (no-op) if neither condition holds - this is the server-side check that
    /// must never be skipped just because the UI already hid the delete button.</summary>
    public async Task<bool> DeleteAsync(int commentId, int requestingUserId, string requestingRole)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.DeletedAt is not null)
            return false;

        var isOwner = comment.UserId == requestingUserId;
        var isModerator = requestingRole is UserRoles.Admin or UserRoles.Curator;
        if (!isOwner && !isModerator)
            return false;

        comment.DeletedAt = DateTimeOffset.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Resolves each comment's author in one batched lookup rather than a SQL join - EF Core's query
    /// translator doesn't handle composing further Where/Select on top of a left-join-into-ValueTuple
    /// projection (confirmed live: "could not be translated" against a query built that way), and a plain
    /// filtered list of comments plus an in-memory dictionary lookup sidesteps that entirely while staying
    /// just as simple.
    /// </summary>
    private static async Task<List<CommentView>> AttachUsersAsync(AiPulseDbContext db, List<CommentRecord> comments)
    {
        var userIds = comments.Select(c => c.UserId).Distinct().ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        return comments.Select(c => ToView(c, users.GetValueOrDefault(c.UserId))).ToList();
    }

    /// <summary>Deleted comments render as "[deleted]" with a blanked body, preserving thread shape (a reply
    /// to a deleted comment still has a parent to attach to) instead of disappearing outright.</summary>
    private static CommentView ToView(CommentRecord c, AppUser? user) => new(
        c.Id, c.ArticleLink, c.ParentCommentId,
        c.DeletedAt is null ? c.Body : "[deleted]",
        c.CreatedAt, c.EditedAt, c.DeletedAt is not null,
        c.UserId,
        user?.Username,
        user?.Role);

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
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Comments'";
            var exists = await check.ExecuteScalarAsync() is not null;

            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "Comments" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Comments" PRIMARY KEY AUTOINCREMENT,
                        "ArticleLink" TEXT NOT NULL,
                        "UserId" INTEGER NOT NULL,
                        "ParentCommentId" INTEGER NULL,
                        "Body" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "EditedAt" TEXT NULL,
                        "DeletedAt" TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                await using var articleIndex = conn.CreateCommand();
                articleIndex.CommandText = """CREATE INDEX "IX_Comments_ArticleLink" ON "Comments" ("ArticleLink")""";
                await articleIndex.ExecuteNonQueryAsync();

                await using var userIndex = conn.CreateCommand();
                userIndex.CommandText = """CREATE INDEX "IX_Comments_UserId" ON "Comments" ("UserId")""";
                await userIndex.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
