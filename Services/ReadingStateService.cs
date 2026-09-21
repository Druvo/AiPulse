using System.Text.Json;
using AiPulse.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace AiPulse.Services;

/// <summary>
/// Persists the signed-in user's reading state (bookmarks, read items, last visit, watchlist, prefs) to a
/// per-user JSON file under App_Data/users/{username}/, so each account gets its own bookmarks/watchlist.
/// Registered Scoped (one instance per Blazor circuit = per signed-in user), and resolves the current
/// username lazily via AuthenticationStateProvider on first use.
///
/// Split across several files by responsibility - this file holds only the shared load/save/lock
/// infrastructure every other part uses; see ReadingStateService.*.cs for Bookmarks, ReadTracking,
/// Learning, FeedFilters, Settings, Fever and Admin. Kept as one partial class (not separate injectable
/// services) because every part reads/writes the *same* in-memory ReadingState + file for the circuit -
/// splitting into independent services would mean either duplicating that load/save/lock machinery per
/// service or introducing a shared store type, either way touching the ~20 files that @inject this
/// service today for no behavioral gain. See design.md / ROADMAP.md if that trade-off is revisited.
/// </summary>
public sealed partial class ReadingStateService
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

    /// <summary>True once loaded for a circuit with no signed-in identity - state stays in-memory only for
    /// the circuit's lifetime, never touching disk (see EnsureLoaded). Public so pages can hide/no-op
    /// account-only UI instead of relying on writes silently going nowhere.</summary>
    public bool IsAnonymous { get; private set; }

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
            var username = authState.User.Identity?.Name;

            // No signed-in identity (News/Explore are public now) - used to fall back to a literal
            // "anonymous" folder, which meant every unauthenticated visitor shared one file and clobbered
            // each other's bookmarks/read-state. Instead: fresh, in-memory-only state for this circuit's
            // lifetime (one browser tab, since this service is Scoped) - never persisted, never shared.
            if (username is null)
            {
                IsAnonymous = true;
                _path = null;
                _state = new ReadingState();
                return;
            }

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
        // Caller holds _lock. Anonymous circuits have no backing file by design - see EnsureLoaded.
        if (_path is null) return;
        File.WriteAllText(_path!, JsonSerializer.Serialize(_state, JsonOpts));
    }
}
