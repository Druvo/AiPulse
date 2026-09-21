using AiPulse.Models;

namespace AiPulse.Services;

public sealed partial class ReadingStateService
{
    public IReadOnlyList<BookmarkItem> Bookmarks
    {
        get { EnsureLoaded(); lock (_lock) return _state!.Bookmarks.OrderByDescending(b => b.SavedAt).ToList(); }
    }

    public bool IsBookmarked(string link)
    {
        EnsureLoaded();
        lock (_lock) return _state!.Bookmarks.Any(b => b.Link == link);
    }

    /// <summary>Adds or removes a bookmark. Returns the new bookmarked state.</summary>
    public bool ToggleBookmark(FeedItem item)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.Bookmarks.FirstOrDefault(b => b.Link == item.Link);
            if (existing is not null)
            {
                _state.Bookmarks.Remove(existing);
                Save();
                return false;
            }
            _state.Bookmarks.Add(new BookmarkItem
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
            Save();
            return true;
        }
    }

    public void RemoveBookmark(string link)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.Bookmarks.FirstOrDefault(b => b.Link == link);
            if (existing is not null) { _state.Bookmarks.Remove(existing); Save(); }
        }
    }
}
