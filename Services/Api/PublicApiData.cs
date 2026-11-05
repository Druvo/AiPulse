using System.Text.Json;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Api;

/// <summary>
/// Everything /api/v1 reads, behind one seam so contract tests can run the real endpoints over fixed data. The public part is
/// deliberately the same data the open pages show (/news, /topics, /releases, /coverage, /glossary, /tools); per-user reading
/// state is only ever read for the account a valid token belongs to.
/// </summary>
public interface IPublicApiData
{
    /// <summary>Saved news history, newest first (headlines and releases).</summary>
    IReadOnlyList<FeedItem> Items { get; }
    /// <summary>Enabled sources only, as /coverage lists them.</summary>
    IReadOnlyList<FeedSource> Sources { get; }
    IReadOnlyList<GlossaryTerm> Glossary { get; }
    IReadOnlyList<ToolEntry> Tools { get; }
    int LearningModules { get; }
    DateTimeOffset? LastPolled { get; }
    int PollMinutes { get; }
    /// <summary>The user's saved reading state (bookmarks, read links), or null when they have none yet.</summary>
    ReadingState? ReadingStateFor(string username);
    Task<bool> DatabaseHealthyAsync(CancellationToken ct);
}

/// <summary>The live implementation over the app's own services.</summary>
public sealed class AppPublicApiData : IPublicApiData
{
    private readonly FeedHistoryService _history;
    private readonly KnowledgeBaseService _kb;
    private readonly FeedAggregatorService _feeds;
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly string _usersDir;

    public AppPublicApiData(FeedHistoryService history, KnowledgeBaseService kb, FeedAggregatorService feeds,
        IOptions<NotificationOptions> notifications, IWebHostEnvironment env, IDbContextFactory<AiPulseDbContext> dbFactory)
    {
        _history = history;
        _kb = kb;
        _feeds = feeds;
        _dbFactory = dbFactory;
        PollMinutes = Math.Max(1, notifications.Value.PollMinutes);
        _usersDir = Path.Combine(env.ContentRootPath, "App_Data", "users");
    }

    public IReadOnlyList<FeedItem> Items => _history.Items;
    public IReadOnlyList<FeedSource> Sources => _kb.Sources.Where(s => s.Enabled).ToList();
    public IReadOnlyList<GlossaryTerm> Glossary => _kb.Glossary;
    public IReadOnlyList<ToolEntry> Tools => _kb.Tools;
    public int LearningModules => _kb.Learning.Count;
    public DateTimeOffset? LastPolled => _feeds.LastFetched;
    public int PollMinutes { get; }

    public ReadingState? ReadingStateFor(string username) => ReadingStateFile.Load(_usersDir, username);

    public async Task<bool> DatabaseHealthyAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Read-only access to App_Data/users/{user}/reading-state.json, keyed exactly as ReadingStateService keys it (invalid file-name
/// characters replaced by '_', lowercased). Never writes: the API has no write endpoints.
/// </summary>
public static class ReadingStateFile
{
    public static string UserKey(string username)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) username = username.Replace(c, '_');
        return username.ToLowerInvariant();
    }

    public static ReadingState? Load(string usersDir, string username)
    {
        var key = UserKey(username);
        if (key is "." or ".." || key.Length == 0) return null;
        var path = Path.Combine(usersDir, key, "reading-state.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ReadingState>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null; // corrupt or mid-write: treat as empty rather than failing the request
        }
    }
}
