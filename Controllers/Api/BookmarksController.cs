using System.Security.Claims;
using AiPulse.Models.Api;
using AiPulse.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiPulse.Controllers.Api;

/// <summary>Reading List (bookmarks) for the mobile app - every action requires a bearer token, since
/// bookmarks are always per-user.</summary>
[ApiController]
[Route("api/mobile/v1/bookmarks")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class BookmarksController : ControllerBase
{
    private readonly MobileReadingStore _reading;

    public BookmarksController(MobileReadingStore reading)
    {
        _reading = reading;
    }

    private string Username => User.FindFirstValue(ClaimTypes.Name)!;

    [HttpGet]
    public ActionResult<IReadOnlyList<BookmarkDto>> List() =>
        _reading.GetBookmarks(Username).Select(b => new BookmarkDto
        {
            Title = b.Title,
            Link = b.Link,
            SourceName = b.SourceName,
            Category = b.Category,
            ContentType = b.ContentType,
            Level = b.Level,
            Tags = b.Tags,
            SavedAt = b.SavedAt,
            Summary = b.Summary,
            ImageUrl = b.ImageUrl
        }).ToList();

    /// <summary>Adds a bookmark, or removes it if the same link is already bookmarked (same toggle
    /// behavior as the website's bookmark button). Returns the new state.</summary>
    [HttpPost]
    public ActionResult<ToggleBookmarkResponse> Toggle([FromBody] BookmarkRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Link) || string.IsNullOrWhiteSpace(body.Title))
            return BadRequest(new { error = "title and link are required" });

        var isBookmarked = _reading.ToggleBookmark(Username, new BookmarkRequestData(
            body.Title, body.Link, body.SourceName, body.Category, body.ContentType, body.Level,
            body.Tags, body.Summary, body.ImageUrl));

        return new ToggleBookmarkResponse { IsBookmarked = isBookmarked };
    }

    [HttpDelete]
    public IActionResult Remove([FromQuery] string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return BadRequest(new { error = "link is required" });
        _reading.RemoveBookmark(Username, link);
        return NoContent();
    }
}

public sealed class BookmarkDto
{
    public required string Title { get; init; }
    public required string Link { get; init; }
    public string SourceName { get; init; } = "";
    public string Category { get; init; } = "";
    public string ContentType { get; init; } = "News";
    public string Level { get; init; } = "Intermediate";
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset SavedAt { get; init; }
    public string? Summary { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed class ToggleBookmarkResponse
{
    public bool IsBookmarked { get; init; }
}
