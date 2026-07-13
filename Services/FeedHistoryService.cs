using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Persists a deduped, rolling history of feed items to disk so the Timeline page has real
/// multi-week data to show, beyond whatever the 15-min live feed cache currently holds.
/// </summary>
public sealed class FeedHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(90);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, FeedItem> _byLink;

    public FeedHistoryService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "feed-history.json");
        _byLink = Load();
    }

    public IReadOnlyList<FeedItem> Items
    {
        get { lock (_lock) return _byLink.Values.OrderByDescending(i => i.Published).ToList(); }
    }

    /// <summary>Adds any not-yet-seen items (by link) and prunes anything older than the retention window.</summary>
    public void Record(IEnumerable<FeedItem> items)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Link) || _byLink.ContainsKey(item.Link))
                    continue;
                _byLink[item.Link] = item;
                changed = true;
            }

            var cutoff = DateTimeOffset.Now - RetainFor;
            foreach (var key in _byLink.Where(kv => kv.Value.Published < cutoff).Select(kv => kv.Key).ToList())
            {
                _byLink.Remove(key);
                changed = true;
            }

            if (changed) Save();
        }
    }

    /// <summary>
    /// One-time repair for items recorded before the "(untitled)" fix (<see cref="FeedAggregatorService.DeriveTitle"/>)
    /// - re-derives a title from the stored summary/source instead of the frozen placeholder. Safe to call on every
    /// startup: a no-op once every stale item has been fixed.
    /// </summary>
    public int RepairUntitledTitles()
    {
        lock (_lock)
        {
            var fixedCount = 0;
            foreach (var key in _byLink.Keys.ToList())
            {
                var item = _byLink[key];
                if (item.Title != "(untitled)") continue;

                var newTitle = FeedAggregatorService.DeriveTitle(null, item.Summary, item.SourceName);
                if (newTitle == item.Title) continue;

                _byLink[key] = item with { Title = newTitle };
                fixedCount++;
            }
            if (fixedCount > 0) Save();
            return fixedCount;
        }
    }

    private Dictionary<string, FeedItem> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<FeedItem>>(File.ReadAllText(_path));
                if (list is not null)
                    return list.Where(i => !string.IsNullOrEmpty(i.Link)).ToDictionary(i => i.Link, i => i);
            }
        }
        catch { /* corrupt file -> start fresh */ }
        return new Dictionary<string, FeedItem>();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path, JsonSerializer.Serialize(_byLink.Values.ToList(), JsonOpts));
    }
}
