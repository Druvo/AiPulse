using System.ComponentModel.DataAnnotations;

namespace AiPulse.Models;

/// <summary>Fixed set of things a user can follow. Kept as plain strings in the DB for simplicity.</summary>
public static class FollowTypes
{
    public const string Source = "Source";
    public const string Tag = "Tag";
}

/// <summary>One user's follow of a source or tag, for the personalized "Following" feed on the News page
/// and for public follower-count aggregates on profile pages. Keyed by AppUser.Id (stable), not Username -
/// unlike ReadingState's per-user JSON files, this needs to answer cross-user queries ("who follows tag X")
/// that a flat file can't without scanning every user's file, so it lives in SQLite instead.</summary>
public sealed class FollowRecord
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string FollowType { get; set; }
    /// <summary>Source name verbatim, or a lowercased tag.</summary>
    public required string FollowKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A comment on an article. ArticleLink is FeedItem.Link - the same key ReadingState already uses
/// for bookmarks/read-state, since AiPulse has no internal article page of its own (Link always points at
/// the original external site). UserId (not a denormalized Username) means a rename never touches this
/// table - the display name is resolved via a join at query time instead.</summary>
public sealed class CommentRecord
{
    public int Id { get; set; }
    public required string ArticleLink { get; set; }
    public int UserId { get; set; }
    /// <summary>Null = top-level comment. One level of replies only - no closure table, no deep threading.</summary>
    public int? ParentCommentId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? EditedAt { get; set; }
    /// <summary>Soft delete - preserves thread shape (a reply to a deleted comment still renders) instead of
    /// leaving a dangling ParentCommentId.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Public-facing profile fields for a user's /u/{username} page. Deliberately its own table, 1:1
/// with AppUser.Id, rather than more fields on ReadingState's private per-user JSON: a stranger's browser
/// hitting the public profile page should never need to open that user's private bookmarks/watchlist/webhook
/// file just to read three public strings, and keying by the stable AppUser.Id (not Username) means a
/// rename touches zero rows here.</summary>
public sealed class UserProfileRecord
{
    /// <summary>PK, 1:1 with AppUser.Id. EF can't infer this by convention (property isn't named Id or
    /// UserProfileRecordId), so it needs the explicit [Key] - AiPulseDbContext has no OnModelCreating,
    /// everything else here relies on convention alone.</summary>
    [Key]
    public int UserId { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A comment joined with its author's current username/role - what UI actually renders. Not stored
/// as-is; produced at query time by CommentService so a rename is reflected immediately with no sync step.</summary>
public sealed record CommentView(
    int Id,
    string ArticleLink,
    int? ParentCommentId,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,
    int UserId,
    string? Username,
    string? Role);
