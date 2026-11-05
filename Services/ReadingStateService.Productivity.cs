using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Read-only, per-user lookups the productivity background services need (they have no signed-in circuit of their own).</summary>
public sealed partial class ReadingStateService
{
    /// <summary>One user's saved reading state read straight from disk, or null when they have none (or it is unreadable).</summary>
    public static ReadingState? ReadStateFor(IWebHostEnvironment env, string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var file = Path.Combine(env.ContentRootPath, "App_Data", "users", SanitizeForPath(username), "reading-state.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(file)); }
        catch { return null; }
    }

    /// <summary>The user's own catch-all webhook from Settings, or null. Used for their personal saved-search alerts only.</summary>
    public static string? GetWebhookUrlFor(IWebHostEnvironment env, string username)
    {
        var url = ReadStateFor(env, username)?.WebhookUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }
}
