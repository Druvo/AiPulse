using System.Collections.Concurrent;
using System.Text.Json;

namespace AiPulse.Services.Ai.Summaries;

public sealed record StoredSummary(string Key, string Text, string Provider, string Model, DateTimeOffset CreatedAt);

/// <summary>
/// Generated text kept so the same item or glossary term is only ever paid for once. Append-only JSON lines on disk, loaded at
/// startup: a corrupt line is skipped rather than failing startup, and the last line for a key wins.
/// </summary>
public sealed class SummaryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _path;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, StoredSummary> _items = new(StringComparer.Ordinal);

    /// <param name="path">File to persist to, or null for a memory-only store.</param>
    public SummaryStore(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<StoredSummary>(line, Json);
                if (entry is { Key.Length: > 0, Text.Length: > 0 }) _items[entry.Key] = entry;
            }
            catch (JsonException)
            {
                // A half-written line from a crash must not take the app down.
            }
        }
    }

    public int Count => _items.Count;

    public StoredSummary? Get(string key) => _items.GetValueOrDefault(key);

    public void Save(StoredSummary entry)
    {
        _items[entry.Key] = entry;
        if (_path is null) return;

        lock (_writeLock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }
}
