using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Cross-user maintenance: startup migrations, admin username-rename, and the watchlist/webhook
/// unions the background watcher needs across every user's file at once.</summary>
public sealed partial class ReadingStateService
{
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
}
