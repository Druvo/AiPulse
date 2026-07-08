using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Persists the single user's reading state (bookmarks, read items, last visit) to a JSON file
/// under App_Data so it survives restarts. No database needed.
/// </summary>
public sealed class ReadingStateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly ReadingState _state;

    public ReadingStateService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "reading-state.json");
        _state = Load();
    }

    public IReadOnlyList<BookmarkItem> Bookmarks
    {
        get { lock (_lock) return _state.Bookmarks.OrderByDescending(b => b.SavedAt).ToList(); }
    }

    public bool IsBookmarked(string link)
    {
        lock (_lock) return _state.Bookmarks.Any(b => b.Link == link);
    }

    /// <summary>Adds or removes a bookmark. Returns the new bookmarked state.</summary>
    public bool ToggleBookmark(FeedItem item)
    {
        lock (_lock)
        {
            var existing = _state.Bookmarks.FirstOrDefault(b => b.Link == item.Link);
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
                SavedAt = DateTimeOffset.Now
            });
            Save();
            return true;
        }
    }

    public void RemoveBookmark(string link)
    {
        lock (_lock)
        {
            var existing = _state.Bookmarks.FirstOrDefault(b => b.Link == link);
            if (existing is not null) { _state.Bookmarks.Remove(existing); Save(); }
        }
    }

    public bool IsRead(string link)
    {
        lock (_lock) return _state.ReadLinks.Contains(link);
    }

    public void MarkRead(string link, bool read)
    {
        lock (_lock)
        {
            var changed = read ? _state.ReadLinks.Add(link) : _state.ReadLinks.Remove(link);
            if (changed) Save();
        }
    }

    /// <summary>Toggles read state and returns the new value.</summary>
    public bool ToggleRead(string link)
    {
        lock (_lock)
        {
            var nowRead = !_state.ReadLinks.Contains(link);
            MarkReadInternal(link, nowRead);
            return nowRead;
        }
    }

    private void MarkReadInternal(string link, bool read)
    {
        // Caller holds _lock.
        var changed = read ? _state.ReadLinks.Add(link) : _state.ReadLinks.Remove(link);
        if (changed) Save();
    }

    // --- Learning Hub progress ---

    public bool IsModuleComplete(string title)
    {
        lock (_lock) return _state.CompletedModules.Contains(title);
    }

    public int CompletedModuleCount
    {
        get { lock (_lock) return _state.CompletedModules.Count; }
    }

    /// <summary>Toggles a module's completed state and returns the new value.</summary>
    public bool ToggleModuleComplete(string title)
    {
        lock (_lock)
        {
            var nowComplete = !_state.CompletedModules.Contains(title);
            if (nowComplete) _state.CompletedModules.Add(title);
            else _state.CompletedModules.Remove(title);
            Save();
            return nowComplete;
        }
    }

    // --- Watchlist ---

    public IReadOnlyList<string> Watchlist
    {
        get { lock (_lock) return _state.Watchlist.ToList(); }
    }

    public void AddKeyword(string keyword)
    {
        keyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return;
        lock (_lock)
        {
            if (!_state.Watchlist.Any(k => k.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                _state.Watchlist.Add(keyword);
                Save();
            }
        }
    }

    public void RemoveKeyword(string keyword)
    {
        lock (_lock)
        {
            var existing = _state.Watchlist.FirstOrDefault(k => k.Equals(keyword, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _state.Watchlist.Remove(existing); Save(); }
        }
    }

    /// <summary>Returns the first watchlist keyword found in the text, or null.</summary>
    public string? MatchWatchlist(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        lock (_lock)
        {
            return _state.Watchlist.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- Export path ---

    public string? ObsidianExportPath
    {
        get { lock (_lock) return _state.ObsidianExportPath; }
    }

    public void SetObsidianExportPath(string? path)
    {
        lock (_lock) { _state.ObsidianExportPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim(); Save(); }
    }

    /// <summary>Returns the previous visit time, then records that the news page was visited now.</summary>
    public DateTimeOffset TouchNewsVisit()
    {
        lock (_lock)
        {
            var previous = _state.LastNewsVisit;
            _state.LastNewsVisit = DateTimeOffset.Now;
            Save();
            return previous;
        }
    }

    private ReadingState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(_path)) ?? new ReadingState();
        }
        catch { /* corrupt file -> start fresh */ }
        return new ReadingState();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOpts));
    }
}
