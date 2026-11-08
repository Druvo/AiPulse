using System.Security.Claims;
using AiPulse.Models;
using AiPulse.Models.Api;
using AiPulse.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiPulse.Controllers.Api;

/// <summary>
/// Read-only news feed for the mobile app - filters/sorts/paginates the same rolling history
/// <see cref="FeedHistoryService"/> already keeps current (the background watcher polls every source),
/// mirroring the filter semantics of Components/Pages/News.razor. Anonymous access is allowed (the News
/// Feed is public on the website too); IsRead/IsBookmarked are only ever true when a bearer token is
/// presented, since those are per-user.
/// </summary>
[ApiController]
[Route("api/v1/news")]
public sealed class NewsController : ControllerBase
{
    private readonly FeedHistoryService _history;
    private readonly KnowledgeBaseService _kb;
    private readonly MobileReadingStore _reading;

    public NewsController(FeedHistoryService history, KnowledgeBaseService kb, MobileReadingStore reading)
    {
        _history = history;
        _kb = kb;
        _reading = reading;
    }

    /// <summary>
    /// The news list and single-item endpoints are anonymous-accessible (no [Authorize]), same as the
    /// website's News page - but still worth enriching with IsRead/IsBookmarked when a token IS presented.
    /// Plain "User.Identity" won't pick that up here: with the cookie scheme as the default, the auth
    /// middleware never runs the "Bearer" handler unless something asks for it, so we ask for it directly.
    /// </summary>
    private async Task<string?> TryGetUsernameAsync()
    {
        var result = await HttpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        return result.Succeeded ? result.Principal?.FindFirstValue(ClaimTypes.Name) : null;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<FeedItemDto>>> List(
        [FromQuery] string? search,
        [FromQuery] string? tag,
        [FromQuery] string? category,
        [FromQuery] string? contentType,
        [FromQuery] string? level,
        [FromQuery] string? source,
        [FromQuery] string? filter, // All | Unread | Read | New
        [FromQuery] string? sort,   // newest (default) | oldest | source | title
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var username = await TryGetUsernameAsync();

        IEnumerable<FeedItem> q = _history.Items.Where(i => i.ContentType != "Release");

        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(i => string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(contentType))
            q = q.Where(i => string.Equals(i.ContentType, contentType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(level))
            q = q.Where(i => string.Equals(i.Level, level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(i => string.Equals(i.SourceName, source, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tag))
            q = q.Where(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            q = q.Where(i =>
                i.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                i.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (username is not null && !string.IsNullOrWhiteSpace(filter))
        {
            q = filter switch
            {
                "Unread" => q.Where(i => !_reading.IsRead(username, i.Link)),
                "Read" => q.Where(i => _reading.IsRead(username, i.Link)),
                _ => q
            };
        }

        q = sort switch
        {
            "oldest" => q.OrderBy(i => i.Published),
            "source" => q.OrderBy(i => i.SourceName, StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.Published),
            "title" => q.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(i => i.Published)
        };

        var all = q.ToList();
        var totalCount = all.Count;
        var slice = all.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => FeedItemDto.From(i,
                isRead: username is not null && _reading.IsRead(username, i.Link),
                isBookmarked: username is not null && _reading.IsBookmarked(username, i.Link)))
            .ToList();

        return new PagedResult<FeedItemDto>
        {
            Items = slice,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    /// <summary>A single item by its original article link (the only stable id a FeedItem has), with full
    /// text included when the source has full-text fetch enabled.</summary>
    [HttpGet("item")]
    public async Task<ActionResult<FeedItemDto>> GetByLink([FromQuery] string link)
    {
        if (string.IsNullOrWhiteSpace(link)) return BadRequest(new { error = "link is required" });
        var item = _history.Items.FirstOrDefault(i => i.Link == link);
        if (item is null) return NotFound();

        var username = await TryGetUsernameAsync();
        return FeedItemDto.From(item,
            isRead: username is not null && _reading.IsRead(username, item.Link),
            isBookmarked: username is not null && _reading.IsBookmarked(username, item.Link),
            includeFullText: true);
    }

    [HttpPost("read")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult MarkRead([FromBody] MarkReadRequest body)
    {
        var username = User.FindFirstValue(ClaimTypes.Name);
        if (username is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Link)) return BadRequest(new { error = "link is required" });
        _reading.MarkRead(username, body.Link);
        return NoContent();
    }

    [HttpGet("sources")]
    public ActionResult<IReadOnlyList<string>> Sources() =>
        _kb.Sources.Where(s => s.Enabled).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    [HttpGet("tags")]
    public ActionResult<IReadOnlyList<string>> Tags() =>
        _history.Items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
}

public sealed class MarkReadRequest
{
    public string Link { get; set; } = "";
}
