using AiPulse.Models;

namespace AiPulse.Services;

public sealed partial class ReadingStateService
{
    public bool IsRead(string link)
    {
        EnsureLoaded();
        lock (_lock) return _state!.ReadLinks.Contains(link);
    }

    /// <summary>Append-only "marked as read" history, for the Reading Stats page.</summary>
    public IReadOnlyList<ReadEvent> ReadHistory
    {
        get { EnsureLoaded(); lock (_lock) return _state!.ReadHistory.ToList(); }
    }

    public void MarkRead(string link, bool read)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var changed = read ? _state!.ReadLinks.Add(link) : _state!.ReadLinks.Remove(link);
            if (changed) Save();
        }
    }

    /// <summary>Bulk unread - the Undo side of <see cref="EnsureReadBulk"/>, same one-save-at-the-end reasoning.</summary>
    public void MarkUnreadBulk(IEnumerable<string> links)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var changed = false;
            foreach (var link in links)
                if (_state!.ReadLinks.Remove(link)) changed = true;
            if (changed) Save();
        }
    }

    /// <summary>
    /// Marks an item read - safe to call every time it's opened (e.g. clicking through to the original
    /// article), unlike <see cref="ToggleRead"/> which would flip an already-read item back to unread.
    /// Appends a <see cref="ReadEvent"/> only on the actual unread-to-read transition, so re-opening an
    /// already-read item doesn't inflate Reading Stats.
    /// </summary>
    public void EnsureRead(FeedItem item)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_state!.ReadLinks.Add(item.Link)) return;
            _state.ReadHistory.Add(new ReadEvent
            {
                Link = item.Link,
                SourceName = item.SourceName,
                ReadingMinutes = item.ReadingMinutes ?? 0,
                Title = item.Title,
                ContentType = item.ContentType,
                Level = item.Level,
                Category = item.Category
            });
            Save();
        }
    }

    /// <summary>
    /// Same as calling <see cref="EnsureRead"/> once per item, but saves exactly once at the end instead of
    /// once per item - the difference between a few milliseconds and (with a large enough batch and reading
    /// history) multiple minutes of the circuit blocked re-serializing the whole state file on every single
    /// item. Found via live testing "Mark all as read" against ~9,000 filtered items, where the per-item
    /// version hung the page for well over a minute. Returns how many items were actually newly marked read.
    /// </summary>
    public int EnsureReadBulk(IEnumerable<FeedItem> items)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var marked = 0;
            foreach (var item in items)
            {
                if (!_state!.ReadLinks.Add(item.Link)) continue;
                _state.ReadHistory.Add(new ReadEvent
                {
                    Link = item.Link,
                    SourceName = item.SourceName,
                    ReadingMinutes = item.ReadingMinutes ?? 0,
                    Title = item.Title,
                    ContentType = item.ContentType,
                    Level = item.Level,
                    Category = item.Category
                });
                marked++;
            }
            if (marked > 0) Save();
            return marked;
        }
    }

    /// <summary>Toggles read state and returns the new value. Appends a <see cref="ReadEvent"/> when transitioning to read, for the Reading Stats page.</summary>
    public bool ToggleRead(FeedItem item)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var nowRead = !_state!.ReadLinks.Contains(item.Link);
            var changed = nowRead ? _state.ReadLinks.Add(item.Link) : _state.ReadLinks.Remove(item.Link);
            if (nowRead)
                _state.ReadHistory.Add(new ReadEvent
                {
                    Link = item.Link,
                    SourceName = item.SourceName,
                    ReadingMinutes = item.ReadingMinutes ?? 0,
                    Title = item.Title,
                    ContentType = item.ContentType,
                    Level = item.Level,
                    Category = item.Category
                });
            if (changed || nowRead) Save();
            return nowRead;
        }
    }

    /// <summary>Read-only peek at the last News-feed visit, for pages (e.g. Dashboard) that want to show a "new since" count without marking the feed as visited.</summary>
    public DateTimeOffset LastNewsVisit
    {
        get { EnsureLoaded(); lock (_lock) return _state!.LastNewsVisit; }
    }

    /// <summary>Returns the previous visit time, then records that the news page was visited now.</summary>
    public DateTimeOffset TouchNewsVisit()
    {
        EnsureLoaded();
        lock (_lock)
        {
            var previous = _state!.LastNewsVisit;
            _state.LastNewsVisit = DateTimeOffset.Now;
            Save();
            return previous;
        }
    }
}
