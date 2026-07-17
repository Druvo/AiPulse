using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Persists a daily snapshot of GitHub Trending's repo leaderboard (per since-window) so past days stay
/// browsable via a calendar, the same way FeedHistoryService keeps News items around past the live cache's
/// reach. GitHubTrendingService only ever holds the latest successful scrape per since-window in memory;
/// this is what makes "what was trending on July 10th" answerable at all.
/// </summary>
public sealed class TrendingRepoHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly int RetainDays = 180;

    private readonly string _path;
    private readonly object _lock = new();
    private List<TrendingRepoSnapshot> _snapshots;

    public TrendingRepoHistoryService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "trending-repo-history.json");
        _snapshots = Load();
    }

    /// <summary>Records today's leaderboard for this since-window, replacing any snapshot already recorded today - repeated refreshes on the same day update today's entry rather than duplicating it. A scrape that came back empty is not recorded, so a transient failure never overwrites a good day with nothing.</summary>
    public void RecordSnapshot(string since, IReadOnlyList<TrendingRepo> repos)
    {
        if (repos.Count == 0) return;
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            _snapshots.RemoveAll(s => s.Date == today && s.Since == since);
            _snapshots.Add(new TrendingRepoSnapshot { Date = today, Since = since, Repos = repos.ToList() });

            var cutoff = today.AddDays(-RetainDays);
            _snapshots.RemoveAll(s => s.Date < cutoff);
            Save();
        }
    }

    public IReadOnlyList<TrendingRepo>? GetSnapshot(DateOnly date, string since)
    {
        lock (_lock) return _snapshots.FirstOrDefault(s => s.Date == date && s.Since == since)?.Repos;
    }

    /// <summary>Recorded dates for this since-window, mapped to repo count - for the calendar's activity dots.</summary>
    public IReadOnlyDictionary<DateOnly, int> AvailableDates(string since)
    {
        lock (_lock) return _snapshots.Where(s => s.Since == since).ToDictionary(s => s.Date, s => s.Repos.Count);
    }

    private List<TrendingRepoSnapshot> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<TrendingRepoSnapshot>>(File.ReadAllText(_path)) ?? new();
        }
        catch { /* corrupt file -> start fresh */ }
        return new List<TrendingRepoSnapshot>();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path, JsonSerializer.Serialize(_snapshots, JsonOpts));
    }
}
