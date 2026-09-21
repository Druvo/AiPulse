using System.Text;
using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Fever API (mobile RSS client compatibility) support. The Fever protocol authenticates via
/// api_key on each request, not the cookie our AuthenticationStateProvider-based instance methods rely
/// on, so the cross-user helpers below operate directly on a specific user's file by key instead of
/// "the current circuit's user".</summary>
public sealed partial class ReadingStateService
{
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
