using System.Text;
using System.Text.Json;
using AiPulse.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace AiPulse.Services;

/// <summary>
/// Persists the signed-in user's reading state (bookmarks, read items, last visit, watchlist, prefs) to a
/// per-user JSON file under App_Data/users/{username}/, so each account gets its own bookmarks/watchlist.
/// Registered Scoped (one instance per Blazor circuit = per signed-in user), and resolves the current
/// username lazily via AuthenticationStateProvider on first use.
/// </summary>
public sealed class ReadingStateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _usersDir;
    private readonly AuthenticationStateProvider _authProvider;
    private readonly object _lock = new();

    private string? _path;
    private ReadingState? _state;

    public ReadingStateService(IWebHostEnvironment env, AuthenticationStateProvider authProvider)
    {
        _usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        _authProvider = authProvider;
    }

    /// <summary>Resolves the current user and loads their state file, once per circuit. Safe to call repeatedly.</summary>
    private void EnsureLoaded()
    {
        if (_state is not null) return;
        lock (_lock)
        {
            if (_state is not null) return;

            // GetAuthenticationStateAsync() is already resolved by the time an [Authorize]-protected page
            // renders (the cookie was validated before the circuit was even created), so blocking here
            // never actually waits on I/O or the renderer - it just unwraps an already-completed Task.
            var authState = _authProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            var username = authState.User.Identity?.Name ?? "anonymous";
            var userKey = SanitizeForPath(username);

            var dir = Path.Combine(_usersDir, userKey);
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "reading-state.json");
            _state = Load();
        }
    }

    private static string SanitizeForPath(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.ToLowerInvariant();
    }

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

    /// <summary>Toggles read state and returns the new value. Appends a <see cref="ReadEvent"/> when transitioning to read, for the Reading Stats page.</summary>
    public bool ToggleRead(FeedItem item)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var nowRead = !_state!.ReadLinks.Contains(item.Link);
            var changed = nowRead ? _state.ReadLinks.Add(item.Link) : _state.ReadLinks.Remove(item.Link);
            if (nowRead)
                _state.ReadHistory.Add(new ReadEvent { Link = item.Link, SourceName = item.SourceName, ReadingMinutes = item.ReadingMinutes ?? 0 });
            if (changed || nowRead) Save();
            return nowRead;
        }
    }

    // --- Learning Hub progress ---

    public bool IsModuleComplete(string title)
    {
        EnsureLoaded();
        lock (_lock) return _state!.CompletedModules.Contains(title);
    }

    public int CompletedModuleCount
    {
        get { EnsureLoaded(); lock (_lock) return _state!.CompletedModules.Count; }
    }

    /// <summary>Toggles a module's completed state and returns the new value.</summary>
    public bool ToggleModuleComplete(string title)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var nowComplete = !_state!.CompletedModules.Contains(title);
            if (nowComplete) _state.CompletedModules.Add(title);
            else _state.CompletedModules.Remove(title);
            Save();
            return nowComplete;
        }
    }

    // --- Watchlist ---

    public IReadOnlyList<string> Watchlist
    {
        get { EnsureLoaded(); lock (_lock) return _state!.Watchlist.ToList(); }
    }

    public void AddKeyword(string keyword)
    {
        EnsureLoaded();
        keyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return;
        lock (_lock)
        {
            if (!_state!.Watchlist.Any(k => k.Equals(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                _state.Watchlist.Add(keyword);
                Save();
            }
        }
    }

    public void RemoveKeyword(string keyword)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.Watchlist.FirstOrDefault(k => k.Equals(keyword, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { _state.Watchlist.Remove(existing); Save(); }
        }
    }

    /// <summary>Returns the first watchlist keyword found in the text, or null.</summary>
    public string? MatchWatchlist(string text)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(text)) return null;
        lock (_lock)
        {
            return _state!.Watchlist.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- Saved searches ("smart folders") ---

    public IReadOnlyList<SavedSearch> SavedSearches
    {
        get { EnsureLoaded(); lock (_lock) return _state!.SavedSearches.ToList(); }
    }

    public void AddSavedSearch(SavedSearch search)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.SavedSearches.Add(search);
            Save();
        }
    }

    public void RemoveSavedSearch(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.SavedSearches.RemoveAll(s => s.Id == id);
            Save();
        }
    }

    // --- Exclude filters ---

    public IReadOnlyList<ExcludeFilter> ExcludeFilters
    {
        get { EnsureLoaded(); lock (_lock) return _state!.ExcludeFilters.ToList(); }
    }

    public void AddExcludeFilter(string pattern, bool isRegex)
    {
        EnsureLoaded();
        pattern = pattern.Trim();
        if (string.IsNullOrWhiteSpace(pattern)) return;
        lock (_lock)
        {
            _state!.ExcludeFilters.Add(new ExcludeFilter { Pattern = pattern, IsRegex = isRegex });
            Save();
        }
    }

    public void RemoveExcludeFilter(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.ExcludeFilters.FirstOrDefault(f => f.Id == id);
            if (existing is not null) { _state.ExcludeFilters.Remove(existing); Save(); }
        }
    }

    /// <summary>True if any exclude filter matches the text. Invalid regexes are treated as non-matching, never throw.</summary>
    public bool IsExcluded(string text)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(text)) return false;
        lock (_lock)
        {
            foreach (var f in _state!.ExcludeFilters)
            {
                if (f.IsRegex)
                {
                    try { if (System.Text.RegularExpressions.Regex.IsMatch(text, f.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true; }
                    catch (ArgumentException) { /* bad user-entered regex - skip it rather than error the page */ }
                }
                else if (text.Contains(f.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // --- Export path ---

    public string? ObsidianExportPath
    {
        get { EnsureLoaded(); lock (_lock) return _state!.ObsidianExportPath; }
    }

    public void SetObsidianExportPath(string? path)
    {
        EnsureLoaded();
        lock (_lock) { _state!.ObsidianExportPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim(); Save(); }
    }

    // --- Welcome banner ---

    public bool WelcomeDismissed
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WelcomeDismissed; }
    }

    public void DismissWelcome()
    {
        EnsureLoaded();
        lock (_lock) { _state!.WelcomeDismissed = true; Save(); }
    }

    // --- Outbound webhook ---

    public string? WebhookUrl
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WebhookUrl; }
    }

    public void SetWebhookUrl(string? url)
    {
        EnsureLoaded();
        lock (_lock) { _state!.WebhookUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(); Save(); }
    }

    // --- Per-keyword webhook routes (additional to the single catch-all WebhookUrl above) ---

    public IReadOnlyList<WebhookRoute> WebhookRoutes
    {
        get { EnsureLoaded(); lock (_lock) return _state!.WebhookRoutes.ToList(); }
    }

    public void AddWebhookRoute(string url, IEnumerable<string> keywords)
    {
        EnsureLoaded();
        url = url.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_lock)
        {
            _state!.WebhookRoutes.Add(new WebhookRoute
            {
                Url = url,
                Keywords = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).ToList()
            });
            Save();
        }
    }

    public void RemoveWebhookRoute(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.WebhookRoutes.RemoveAll(r => r.Id == id);
            Save();
        }
    }

    /// <summary>
    /// Every user's webhook routes, including their single legacy <see cref="WebhookUrl"/> represented as
    /// a catch-all route (empty keywords) - lets <c>FeedWatcherService</c> treat both uniformly. Not scoped
    /// to one signed-in user's circuit, same rationale as <see cref="GetAllUsersWatchlist"/>.
    /// </summary>
    public static IReadOnlyList<WebhookRoute> GetAllWebhookRoutes(IWebHostEnvironment env)
    {
        var usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        var result = new List<WebhookRoute>();
        if (!Directory.Exists(usersDir))
            return result;

        foreach (var dir in Directory.GetDirectories(usersDir))
        {
            var file = Path.Combine(dir, "reading-state.json");
            if (!File.Exists(file)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(file));
                if (state is null) continue;

                if (!string.IsNullOrWhiteSpace(state.WebhookUrl))
                    result.Add(new WebhookRoute { Url = state.WebhookUrl, Keywords = new() });
                result.AddRange(state.WebhookRoutes.Where(r => !string.IsNullOrWhiteSpace(r.Url)));
            }
            catch { /* corrupt file for that user -> skip */ }
        }
        return result;
    }

    // --- Fever API (mobile RSS client compatibility) ---

    public string? FeverApiKey
    {
        get { EnsureLoaded(); lock (_lock) return _state!.FeverApiKey; }
    }

    /// <summary>Sets the Fever API key from a plaintext password the user chose - only the MD5 digest is stored.</summary>
    public void SetFeverApiPassword(string username, string? password)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.FeverApiKey = string.IsNullOrWhiteSpace(password) ? null : ComputeFeverKey(username, password);
            Save();
        }
    }

    public static string ComputeFeverKey(string username, string password)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // --- Dashboard widgets ---

    public IReadOnlyList<DashboardWidget> DashboardWidgets
    {
        get { EnsureLoaded(); lock (_lock) return _state!.DashboardWidgets.ToList(); }
    }

    public void AddDashboardWidget(string title, string type, string content)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _state!.DashboardWidgets.Add(new DashboardWidget { Title = title.Trim(), Type = type, Content = content.Trim() });
            Save();
        }
    }

    public void RemoveDashboardWidget(Guid id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var existing = _state!.DashboardWidgets.FirstOrDefault(w => w.Id == id);
            if (existing is not null) { _state.DashboardWidgets.Remove(existing); Save(); }
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

    private ReadingState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(_path!)) ?? new ReadingState();
        }
        catch { /* corrupt file -> start fresh */ }
        return new ReadingState();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path!, JsonSerializer.Serialize(_state, JsonOpts));
    }

    /// <summary>
    /// One-time upgrade path: before multi-user support, everyone's bookmarks/watchlist lived in a single
    /// App_Data/reading-state.json. Moves that file into the bootstrapped Admin's per-user folder so the
    /// existing owner doesn't lose their data. No-op if there's nothing to migrate or it already ran.
    /// </summary>
    public static void MigrateLegacyStateFile(IWebHostEnvironment env, string adminUsername)
    {
        var legacyPath = Path.Combine(env.ContentRootPath, "App_Data", "reading-state.json");
        if (!File.Exists(legacyPath)) return;

        var dir = Path.Combine(env.ContentRootPath, "App_Data", "users", SanitizeForPath(adminUsername));
        var newPath = Path.Combine(dir, "reading-state.json");
        if (File.Exists(newPath)) return; // already migrated

        Directory.CreateDirectory(dir);
        File.Copy(legacyPath, newPath);
        File.Move(legacyPath, legacyPath + ".migrated");
    }

    /// <summary>
    /// Moves a renamed user's per-user folder (bookmarks, watchlist, exclude filters, etc.) from the old
    /// username's key to the new one, so a rename doesn't look like data loss. Also clears FeverApiKey -
    /// it's MD5(username:password), so it's silently invalid under the new username and there's no way to
    /// recompute it without the plaintext password; the user just needs to re-set it in Settings if they
    /// use a Fever-compatible mobile client.
    /// </summary>
    public static void RenameUserFolder(IWebHostEnvironment env, string oldUsername, string newUsername)
    {
        var oldKey = SanitizeForPath(oldUsername);
        var newKey = SanitizeForPath(newUsername);
        if (oldKey == newKey) return;

        var usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        var oldDir = Path.Combine(usersDir, oldKey);
        var newDir = Path.Combine(usersDir, newKey);
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

        Directory.Move(oldDir, newDir);

        var statePath = Path.Combine(newDir, "reading-state.json");
        if (!File.Exists(statePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(statePath));
            if (state is null || state.FeverApiKey is null) return;
            state.FeverApiKey = null;
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { /* corrupt file - leave as-is, nothing more we can safely do here */ }
    }

    /// <summary>
    /// Scans every user's reading-state file and unions their watchlists. Used only by the background
    /// FeedWatcherService, which has no single "current user" - a keyword watched by any user is enough
    /// to raise a shared desktop-notification alert. Per-user ⭐ highlighting on the News page still uses
    /// each user's own list via <see cref="MatchWatchlist"/> above.
    /// </summary>
    public static IReadOnlyList<string> GetAllUsersWatchlist(IWebHostEnvironment env)
    {
        var usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(usersDir))
            return result.ToList();

        foreach (var dir in Directory.GetDirectories(usersDir))
        {
            var file = Path.Combine(dir, "reading-state.json");
            if (!File.Exists(file)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(file));
                if (state?.Watchlist is { Count: > 0 })
                    foreach (var k in state.Watchlist) result.Add(k);
            }
            catch { /* corrupt file for that user -> skip */ }
        }
        return result.ToList();
    }

    /// <summary>
    /// One-time repair for bookmarks saved before the "(untitled)" fix - re-titles them from the (now-repaired)
    /// history entry for the same link, or falls back to naming the source. Safe to call on every startup.
    /// </summary>
    public static int RepairUntitledBookmarksForAllUsers(IWebHostEnvironment env, FeedHistoryService history)
    {
        var usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        if (!Directory.Exists(usersDir)) return 0;

        var fixedCount = 0;
        foreach (var dir in Directory.GetDirectories(usersDir))
        {
            var path = Path.Combine(dir, "reading-state.json");
            if (!File.Exists(path)) continue;

            ReadingState? state;
            try { state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path)); }
            catch { continue; }
            if (state is null) continue;

            var changed = false;
            for (var i = 0; i < state.Bookmarks.Count; i++)
            {
                var b = state.Bookmarks[i];
                if (b.Title != "(untitled)") continue;

                var match = history.Items.FirstOrDefault(it => it.Link == b.Link);
                var newTitle = match is not null && match.Title != "(untitled)"
                    ? match.Title
                    : FeedAggregatorService.DeriveTitle(null, "", b.SourceName);
                if (newTitle == b.Title) continue;

                state.Bookmarks[i] = new BookmarkItem
                {
                    Title = newTitle,
                    Link = b.Link,
                    SourceName = b.SourceName,
                    Category = b.Category,
                    ContentType = b.ContentType,
                    Level = b.Level,
                    Tags = b.Tags,
                    SavedAt = b.SavedAt,
                    Summary = b.Summary,
                    ImageUrl = b.ImageUrl
                };
                fixedCount++;
                changed = true;
            }

            if (changed)
                File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        return fixedCount;
    }

    // --- Fever API cross-user helpers (the Fever protocol authenticates via api_key on each request,
    // not the cookie our AuthenticationStateProvider-based instance methods rely on, so these operate
    // directly on a specific user's file by key instead of "the current circuit's user"). ---

    /// <summary>Finds which user's sanitized folder key owns this Fever API key digest, or null.</summary>
    public static string? FindUserKeyByFeverApiKey(IWebHostEnvironment env, string apiKey)
    {
        var usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
        if (!Directory.Exists(usersDir)) return null;

        foreach (var dir in Directory.GetDirectories(usersDir))
        {
            var file = Path.Combine(dir, "reading-state.json");
            if (!File.Exists(file)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(file));
                if (!string.IsNullOrEmpty(state?.FeverApiKey) &&
                    string.Equals(state.FeverApiKey, apiKey, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            }
            catch { /* corrupt file for that user -> skip */ }
        }
        return null;
    }

    private static string UserKeyPath(IWebHostEnvironment env, string userKey) =>
        Path.Combine(env.ContentRootPath, "App_Data", "users", userKey, "reading-state.json");

    public static HashSet<string> GetReadLinksForUserKey(IWebHostEnvironment env, string userKey)
    {
        var path = UserKeyPath(env, userKey);
        if (!File.Exists(path)) return new HashSet<string>();
        try { return JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path))?.ReadLinks ?? new(); }
        catch { return new HashSet<string>(); }
    }

    public static HashSet<string> GetBookmarkedLinksForUserKey(IWebHostEnvironment env, string userKey)
    {
        var path = UserKeyPath(env, userKey);
        if (!File.Exists(path)) return new HashSet<string>();
        try
        {
            var state = JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path));
            return state?.Bookmarks.Select(b => b.Link).ToHashSet() ?? new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    public static void MarkReadForUserKey(IWebHostEnvironment env, string userKey, string link, bool read)
    {
        MutateUserFile(env, userKey, state =>
        {
            if (read) state.ReadLinks.Add(link); else state.ReadLinks.Remove(link);
        });
    }

    public static void MarkSavedForUserKey(IWebHostEnvironment env, string userKey, string link, bool saved, FeedItem? item)
    {
        MutateUserFile(env, userKey, state =>
        {
            var existing = state.Bookmarks.FirstOrDefault(b => b.Link == link);
            if (saved && existing is null && item is not null)
            {
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
            }
            else if (!saved && existing is not null)
            {
                state.Bookmarks.Remove(existing);
            }
        });
    }

    private static void MutateUserFile(IWebHostEnvironment env, string userKey, Action<ReadingState> mutate)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data", "users", userKey);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "reading-state.json");

        ReadingState state;
        try { state = File.Exists(path) ? JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path)) ?? new() : new(); }
        catch { state = new(); }

        mutate(state);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
