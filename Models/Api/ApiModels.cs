namespace AiPulse.Models.Api;

/// <summary>Request body for POST /api/mobile/v1/auth/login.</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Authenticator code or a recovery code, only needed when the account has two-step sign-in on.</summary>
    public string? Code { get; set; }
}

/// <summary>Response for a successful login - the bearer token the app attaches to every later request.</summary>
public sealed class LoginResponse
{
    public required string Token { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Flat, JSON-friendly projection of <see cref="FeedItem"/> plus the requesting user's own
/// read/bookmark state - the app has no other way to know those, since they're per-user.</summary>
public sealed class FeedItemDto
{
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset Published { get; init; }
    public required string SourceName { get; init; }
    public required string Category { get; init; }
    public string ContentType { get; init; } = "News";
    public string Level { get; init; } = "Intermediate";
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string[] AlsoSeenOn { get; init; } = Array.Empty<string>();
    public string? FullText { get; init; }
    public string? ImageUrl { get; init; }
    public string? Author { get; init; }
    public int? ReadingMinutes { get; init; }
    public bool IsRead { get; init; }
    public bool IsBookmarked { get; init; }

    public static FeedItemDto From(FeedItem item, bool isRead, bool isBookmarked, bool includeFullText = false) => new()
    {
        Title = item.Title,
        Link = item.Link,
        Summary = item.Summary,
        Published = item.Published,
        SourceName = item.SourceName,
        Category = item.Category,
        ContentType = item.ContentType,
        Level = item.Level,
        Tags = item.Tags,
        AlsoSeenOn = item.AlsoSeenOn,
        FullText = includeFullText ? item.FullText : null,
        ImageUrl = item.ImageUrl,
        Author = item.Author,
        ReadingMinutes = item.ReadingMinutes,
        IsRead = isRead,
        IsBookmarked = isBookmarked
    };
}

/// <summary>Generic paginated result wrapper used by every list endpoint.</summary>
public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Request body for POST /api/v1/bookmarks - enough of the item's own fields to render a
/// Reading List card without a second fetch, mirroring <see cref="BookmarkItem"/>.</summary>
public sealed class BookmarkRequest
{
    public required string Title { get; set; }
    public required string Link { get; set; }
    public string SourceName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ContentType { get; set; } = "News";
    public string Level { get; set; } = "Intermediate";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Summary { get; set; }
    public string? ImageUrl { get; set; }
}
