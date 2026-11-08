using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Per-user bookmarks/read-state for the mobile API. Reads and writes the exact same
/// App_Data/users/{username}/reading-state.json file <see cref="ReadingStateService"/> uses, so bookmarks
/// made in the app show up on the website and vice versa - but doesn't depend on
/// ReadingStateService itself, since that resolves "who's signed in" via Blazor's
/// AuthenticationStateProvider (tied to a Server-render circuit), which an ASP.NET Core Web API
/// controller under JWT bearer auth never has. Loads fresh from disk on every call instead of caching
/// in memory for a circuit's lifetime, since a singleton here could otherwise go stale across requests
/// from different users.
/// </summary>
public sealed class MobileReadingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly object FileLock = new();

    private readonly string _usersDir;

    public MobileReadingStore(IWebHostEnvironment env)
    {
        _usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
    }

    private static string SanitizeForPath(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.ToLowerInvariant();
    }

    private string PathFor(string username)
    {
        var dir = Path.Combine(_usersDir, SanitizeForPath(username));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "reading-state.json");
    }

    private ReadingState Load(string username)
    {
        var path = PathFor(username);
        lock (FileLock)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path)) ?? new ReadingState();
            }
            catch { /* corrupt file -> start fresh, same as ReadingStateService */ }
            return new ReadingState();
        }
    }

    private void Save(string username, ReadingState state)
    {
        var path = PathFor(username);
        lock (FileLock) File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
    }

    public IReadOnlyList<BookmarkItem> GetBookmarks(string username) =>
        Load(username).Bookmarks.OrderByDescending(b => b.SavedAt).ToList();

    public bool IsBookmarked(string username, string link) =>
        Load(username).Bookmarks.Any(b => b.Link == link);

    public bool IsRead(string username, string link) =>
        Load(username).ReadLinks.Contains(link);

    /// <summary>Adds or removes a bookmark. Returns the new bookmarked state.</summary>
    public bool ToggleBookmark(string username, BookmarkRequestData item)
    {
        var state = Load(username);
        var existing = state.Bookmarks.FirstOrDefault(b => b.Link == item.Link);
        if (existing is not null)
        {
            state.Bookmarks.Remove(existing);
            Save(username, state);
            return false;
        }

        state.Bookmarks.Add(new BookmarkItem
        {
            Title = item.Title,
            Link = item.Link,
            SourceName = item.SourceName,
            Category = item.Category,
            ContentType = item.ContentType,
            Level = item.Level,
            Tags = item.Tags,
            SavedAt = DateTimeOffset.Now,
            Summary = item.Summary,
            ImageUrl = item.ImageUrl
        });
        Save(username, state);
        return true;
    }

    public void RemoveBookmark(string username, string link)
    {
        var state = Load(username);
        var existing = state.Bookmarks.FirstOrDefault(b => b.Link == link);
        if (existing is null) return;
        state.Bookmarks.Remove(existing);
        Save(username, state);
    }

    public void MarkRead(string username, string link)
    {
        var state = Load(username);
        if (state.ReadLinks.Add(link)) Save(username, state);
    }
}

/// <summary>Plain data carrier so <see cref="MobileReadingStore"/> doesn't need to depend on the API
/// layer's request model.</summary>
public sealed record BookmarkRequestData(
    string Title, string Link, string SourceName, string Category,
    string ContentType, string Level, string[] Tags, string? Summary, string? ImageUrl);
