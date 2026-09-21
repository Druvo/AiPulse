using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Everything that shapes what a user sees in their own News feed: keywords to highlight/notify
/// on, sources they've muted, exclude patterns, and saved searches.</summary>
public sealed partial class ReadingStateService
{
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

    // --- Per-user muted sources (Sources themselves stay global/admin-managed - see MutedSources doc comment) ---

    public IReadOnlyCollection<string> MutedSources
    {
        get { EnsureLoaded(); lock (_lock) return _state!.MutedSources.ToList(); }
    }

    public bool IsSourceMuted(string sourceName)
    {
        EnsureLoaded();
        lock (_lock) return _state!.MutedSources.Contains(sourceName);
    }

    /// <summary>Toggles whether a source is muted for this user and returns the new state.</summary>
    public bool ToggleMutedSource(string sourceName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var nowMuted = !_state!.MutedSources.Contains(sourceName);
            if (nowMuted) _state.MutedSources.Add(sourceName);
            else _state.MutedSources.Remove(sourceName);
            Save();
            return nowMuted;
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
}
