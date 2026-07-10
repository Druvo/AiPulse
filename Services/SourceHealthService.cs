using System.Text.Json;

namespace AiPulse.Services;

/// <summary>
/// Persists a rolling daily success/fail record per source, so the Sources page can show a real uptime
/// history instead of just "currently failing" (which resets on every restart). One entry per
/// (source, calendar day): true if any fetch that day succeeded, false if every attempt failed.
/// </summary>
public sealed class SourceHealthService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int RetainDays = 30;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, bool>> _data;

    public SourceHealthService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "source-health.json");
        _data = Load();
    }

    public void RecordResult(string sourceName, bool success)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            if (!_data.TryGetValue(sourceName, out var days))
                _data[sourceName] = days = new Dictionary<string, bool>();

            days[today] = success;
            Prune(days);
            Save();
        }
    }

    /// <summary>Healthy-days / total-recorded-days over the last N days. (0, 0) if nothing recorded yet.</summary>
    public (int HealthyDays, int TotalDays) GetSummary(string sourceName, int days = 30)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(sourceName, out var history))
                return (0, 0);

            var cutoff = DateTime.Now.AddDays(-days).Date;
            var relevant = history.Where(kv => DateTime.TryParse(kv.Key, out var d) && d.Date >= cutoff).ToList();
            return (relevant.Count(kv => kv.Value), relevant.Count);
        }
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

    private Dictionary<string, Dictionary<string, bool>> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, Dictionary<string, bool>>();
        }
        catch { /* corrupt file -> start fresh */ }
        return new Dictionary<string, Dictionary<string, bool>>();
    }

    private void Save()
    {
        // Caller holds _lock.
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOpts));
    }
}
