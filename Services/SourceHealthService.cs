using System.Text.Json;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Persists a rolling daily success/fail record per source, plus the most recent attempt's outcome
/// (error message, response time) and a smoothed average response time - so the Sources page and the
/// Source Health dashboard can show real history instead of just "currently failing" (which would reset
/// on every restart). One day-bucket entry per (source, calendar day): true if any fetch that day
/// succeeded, false if every attempt failed.
/// </summary>
public sealed class SourceHealthService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int RetainDays = 30;
    // Exponential moving average weight for response time - smooths single-request noise without needing
    // to store a full time series per source.
    private const double AvgWeight = 0.3;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, SourceHealthEntry> _data;

    public SourceHealthService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "source-health.json");
        _data = Load();
    }

    /// <summary><paramref name="elapsed"/> and <paramref name="error"/> are optional so existing call sites without timing info still work.</summary>
    public void RecordResult(string sourceName, bool success, TimeSpan? elapsed = null, string? error = null)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(sourceName, out var entry))
                _data[sourceName] = entry = new SourceHealthEntry();

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            entry.Days[today] = success;
            Prune(entry.Days);

            entry.LastAttempt = DateTimeOffset.Now;
            entry.LastSuccess = success;
            entry.LastError = success ? null : error;
            if (elapsed is { } e)
            {
                entry.LastResponseMs = e.TotalMilliseconds;
                entry.AvgResponseMs = entry.AvgResponseMs is { } avg
                    ? avg * (1 - AvgWeight) + e.TotalMilliseconds * AvgWeight
                    : e.TotalMilliseconds;
            }

            Save();
        }
    }

    /// <summary>Healthy-days / total-recorded-days over the last N days. (0, 0) if nothing recorded yet.</summary>
    public (int HealthyDays, int TotalDays) GetSummary(string sourceName, int days = 30)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(sourceName, out var entry))
                return (0, 0);

            var cutoff = DateTime.Now.AddDays(-days).Date;
            var relevant = entry.Days.Where(kv => DateTime.TryParse(kv.Key, out var d) && d.Date >= cutoff).ToList();
            return (relevant.Count(kv => kv.Value), relevant.Count);
        }
    }

    /// <summary>Full point-in-time snapshot for one source, for the Source Health dashboard. Null if never attempted.</summary>
    public SourceHealthSnapshot? GetSnapshot(string sourceName, int days = 30)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(sourceName, out var entry))
                return null;

            var (healthy, total) = GetSummaryUnlocked(entry, days);
            return new SourceHealthSnapshot
            {
                SourceName = sourceName,
                HealthyDays = healthy,
                TotalDays = total,
                LastAttempt = entry.LastAttempt,
                LastSuccess = entry.LastSuccess,
                LastError = entry.LastError,
                LastResponseMs = entry.LastResponseMs,
                AvgResponseMs = entry.AvgResponseMs
            };
        }
    }

    private static (int HealthyDays, int TotalDays) GetSummaryUnlocked(SourceHealthEntry entry, int days)
    {
        var cutoff = DateTime.Now.AddDays(-days).Date;
        var relevant = entry.Days.Where(kv => DateTime.TryParse(kv.Key, out var d) && d.Date >= cutoff).ToList();
        return (relevant.Count(kv => kv.Value), relevant.Count);
    }

    private static void Prune(Dictionary<string, bool> days)
    {
        var cutoff = DateTime.Now.AddDays(-RetainDays).Date;
        foreach (var key in days.Keys.ToList())
        {
            if (DateTime.TryParse(key, out var d) && d.Date < cutoff)
                days.Remove(key);
        }
    }

    private Dictionary<string, SourceHealthEntry> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, SourceHealthEntry>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, SourceHealthEntry>();
        }
        catch { /* corrupt or pre-existing old-shape file -> start fresh, next result overwrites it */ }
        return new Dictionary<string, SourceHealthEntry>();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOpts));
    }

    private sealed class SourceHealthEntry
    {
        public Dictionary<string, bool> Days { get; set; } = new();
        public DateTimeOffset? LastAttempt { get; set; }
        public bool? LastSuccess { get; set; }
        public string? LastError { get; set; }
        public double? LastResponseMs { get; set; }
        public double? AvgResponseMs { get; set; }
    }
}
