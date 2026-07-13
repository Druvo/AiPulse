using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Implements the Fever API (the sync protocol originally from the Fever RSS reader, still supported as
/// a compatibility target by many mobile RSS clients - Reeder, ReadKit, Fiery Feeds, etc.) so AiPulse's
/// feed can be read from those apps. Auth is per-user via a separate "Fever API password" (Settings page)
/// since the protocol requires an MD5("username:password") the server can compare directly - something
/// we can't derive from the PBKDF2 hash used for the real login.
///
/// Stable numeric item/feed ids are required by the protocol for since_id paging and read/saved tracking,
/// but AiPulse's FeedItem is keyed by link (a string) - a small persisted link-to-id map bridges the two,
/// assigning the next id the first time a link is seen (so ids grow roughly in discovery order, which is
/// exactly what since_id-based incremental sync needs).
/// </summary>
public sealed class FeverApiService
{
    private static readonly string[] Categories = { "News", "Research", "Tools", "Community" };

    private readonly IWebHostEnvironment _env;
    private readonly KnowledgeBaseService _kb;
    private readonly FeedHistoryService _history;
    private readonly string _idMapPath;
    private readonly object _idLock = new();
    private Dictionary<string, int>? _idByLink;
    private int _nextId = 1;

    public FeverApiService(IWebHostEnvironment env, KnowledgeBaseService kb, FeedHistoryService history)
    {
        _env = env;
        _kb = kb;
        _history = history;
        _idMapPath = Path.Combine(env.ContentRootPath, "App_Data", "fever-item-ids.json");
    }

    /// <summary>Validates the client's api_key against every user's stored Fever key. Returns the matching user's folder key, or null.</summary>
    public string? Authenticate(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? null : ReadingStateService.FindUserKeyByFeverApiKey(_env, apiKey);

    /// <summary>Builds the JSON response for a request. userKey is null for the unauthenticated capability ping.</summary>
    public async Task<object> BuildResponseAsync(string? userKey, IQueryCollection query, IFormCollection? form)
    {
        string? Param(string name) => form?[name].FirstOrDefault() ?? query[name].FirstOrDefault();
        bool Has(string name) => form?.ContainsKey(name) == true || query.ContainsKey(name);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = new Dictionary<string, object?>
        {
            ["api_version"] = 3,
            ["auth"] = userKey is null ? 0 : 1,
            ["last_refreshed_on_time"] = now
        };
        if (userKey is null)
            return result;

        // Mark actions come first since a client may combine ?mark=... with a subsequent resource request.
        if (Has("mark"))
            await HandleMarkAsync(userKey, Param);

        if (Has("groups"))
        {
            result["groups"] = Categories.Select((c, i) => new { id = i + 1, title = c }).ToList();
            result["feeds_groups"] = BuildFeedsGroups();
        }

        if (Has("feeds"))
        {
            result["feeds"] = _kb.SourceRecords.Select(s => new
            {
                id = s.Id,
                favicon_id = 0,
                title = s.Name,
                url = s.Url,
                site_url = s.Url,
                is_spark = 0,
                last_updated_on_time = now
            }).ToList();
            result["feeds_groups"] = BuildFeedsGroups();
        }

        if (Has("favicons"))
            result["favicons"] = Array.Empty<object>();

        if (Has("items"))
        {
            var (items, total) = BuildItems(userKey, Param);
            result["items"] = items;
            result["total_items"] = total;
        }

        if (Has("unread_item_ids"))
        {
            var read = ReadingStateService.GetReadLinksForUserKey(_env, userKey);
            var ids = _history.Items.Where(i => !read.Contains(i.Link)).Select(i => GetOrAssignId(i.Link));
            result["unread_item_ids"] = string.Join(',', ids);
        }

        if (Has("saved_item_ids"))
        {
            var saved = ReadingStateService.GetBookmarkedLinksForUserKey(_env, userKey);
            var ids = _history.Items.Where(i => saved.Contains(i.Link)).Select(i => GetOrAssignId(i.Link));
            result["saved_item_ids"] = string.Join(',', ids);
        }

        return result;
    }

    private List<object> BuildFeedsGroups() =>
        Categories.Select((c, i) => new
        {
            group_id = i + 1,
            feed_ids = string.Join(',', _kb.SourceRecords.Where(s => s.Category == c).Select(s => s.Id))
        }).Cast<object>().ToList();

    private (List<object> Items, int Total) BuildItems(string userKey, Func<string, string?> param)
    {
        var read = ReadingStateService.GetReadLinksForUserKey(_env, userKey);
        var saved = ReadingStateService.GetBookmarkedLinksForUserKey(_env, userKey);
        var byName = _kb.SourceRecords.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

        // Assign/lookup ids for every item once, then sort ascending by id - id order tracks discovery
        // order, which is what since_id-based incremental sync actually needs.
        var withIds = _history.Items.Select(i => (Item: i, Id: GetOrAssignId(i.Link))).OrderBy(t => t.Id).ToList();

        int.TryParse(param("since_id"), out var sinceId);
        int.TryParse(param("max_id"), out var maxId);
        var withIdsFilter = param("with_ids");

        IEnumerable<(FeedItem Item, int Id)> filtered = withIds;
        if (!string.IsNullOrWhiteSpace(withIdsFilter))
        {
            var wanted = withIdsFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : -1).ToHashSet();
            filtered = filtered.Where(t => wanted.Contains(t.Id));
        }
        else if (maxId > 0)
        {
            filtered = filtered.Where(t => t.Id < maxId).OrderByDescending(t => t.Id);
        }
        else if (sinceId > 0)
        {
            filtered = filtered.Where(t => t.Id > sinceId);
        }

        var page = filtered.Take(50).ToList();
        var items = page.Select(t => (object)new
        {
            id = t.Id,
            feed_id = byName.GetValueOrDefault(t.Item.SourceName, 0),
            title = t.Item.Title,
            author = t.Item.SourceName,
            html = string.IsNullOrWhiteSpace(t.Item.Summary) ? t.Item.Title : t.Item.Summary,
            url = t.Item.Link,
            is_saved = saved.Contains(t.Item.Link) ? 1 : 0,
            is_read = read.Contains(t.Item.Link) ? 1 : 0,
            created_on_time = t.Item.Published.ToUnixTimeSeconds()
        }).ToList();

        return (items, withIds.Count);
    }

    private async Task HandleMarkAsync(string userKey, Func<string, string?> param)
    {
        var type = param("mark");
        var asAction = param("as");
        var idStr = param("id");
        if (type != "item" || string.IsNullOrEmpty(asAction) || !int.TryParse(idStr, out var id))
            return; // feed/group bulk-mark not implemented - see README limitation note

        var link = FindLinkById(id);
        if (link is null) return;

        switch (asAction)
        {
            case "read":
                ReadingStateService.MarkReadForUserKey(_env, userKey, link, true);
                break;
            case "unread":
                ReadingStateService.MarkReadForUserKey(_env, userKey, link, false);
                break;
            case "saved":
                ReadingStateService.MarkSavedForUserKey(_env, userKey, link, true, _history.Items.FirstOrDefault(i => i.Link == link));
                break;
            case "unsaved":
                ReadingStateService.MarkSavedForUserKey(_env, userKey, link, false, null);
                break;
        }
        await Task.CompletedTask;
    }

    private string? FindLinkById(int id)
    {
        lock (_idLock)
        {
            EnsureIdMapLoaded();
            return _idByLink!.FirstOrDefault(kv => kv.Value == id).Key;
        }
    }

    private int GetOrAssignId(string link)
    {
        lock (_idLock)
        {
            EnsureIdMapLoaded();
            if (_idByLink!.TryGetValue(link, out var existing))
                return existing;

            var id = _nextId++;
            _idByLink[link] = id;
            SaveIdMap();
            return id;
        }
    }

    private void EnsureIdMapLoaded()
    {
        if (_idByLink is not null) return;
        try
        {
            if (File.Exists(_idMapPath))
            {
                _idByLink = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_idMapPath)) ?? new();
                _nextId = _idByLink.Count == 0 ? 1 : _idByLink.Values.Max() + 1;
                return;
            }
        }
        catch { /* corrupt file -> start fresh */ }
        _idByLink = new Dictionary<string, int>();
    }

    private void SaveIdMap()
    {
        // Caller holds _idLock.
        Directory.CreateDirectory(Path.GetDirectoryName(_idMapPath)!);
        File.WriteAllText(_idMapPath, JsonSerializer.Serialize(_idByLink));
    }
}
