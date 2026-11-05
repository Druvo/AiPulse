using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// In-memory hub for alerts about big releases and watchlist hits. The background
/// <see cref="FeedWatcherService"/> pushes alerts here; the UI bell subscribes to <see cref="OnChange"/>.
///
/// Alerts with an <see cref="Alert.Audience"/> (personal ones, e.g. saved-search hits) are kept apart from the shared list: they are
/// left out of <see cref="Recent"/>, <see cref="UnreadCount"/> and <see cref="OnChange"/>, and only surface through the
/// audience-aware members (<see cref="RecentFor"/>, <see cref="UnreadCountFor"/>, <see cref="OnUserAlerts"/>). That way the existing
/// bell, which predates audiences, can never leak one user's alerts to everyone.
/// </summary>
public sealed class NotificationService
{
    private const int MaxKept = 100;
    private const int MaxKeptTargeted = 300;
    private readonly object _lock = new();
    private readonly List<Alert> _alerts = new();
    private readonly List<Alert> _targeted = new();

    /// <summary>Raised when new shared alerts are added. Args: the newly added alerts (never targeted ones).</summary>
    public event Action<IReadOnlyList<Alert>>? OnChange;

    /// <summary>Raised when alerts for one user are added. Args: the username and that user's new alerts.</summary>
    public event Action<string, IReadOnlyList<Alert>>? OnUserAlerts;

    public IReadOnlyList<Alert> Recent
    {
        get { lock (_lock) return _alerts.OrderByDescending(a => a.CreatedAt).Take(30).ToList(); }
    }

    public int UnreadCount
    {
        get { lock (_lock) return _alerts.Count(a => !a.Read); }
    }

    /// <summary>Shared alerts plus the given user's own, newest first. A null/empty username gets only the shared ones.</summary>
    public IReadOnlyList<Alert> RecentFor(string? username, int take = 30)
    {
        lock (_lock)
            return _alerts.Concat(OwnLocked(username)).OrderByDescending(a => a.CreatedAt).Take(take).ToList();
    }

    public int UnreadCountFor(string? username)
    {
        lock (_lock) return _alerts.Count(a => !a.Read) + OwnLocked(username).Count(a => !a.Read);
    }

    public void Add(IReadOnlyList<Alert> newAlerts)
    {
        if (newAlerts.Count == 0) return;
        var shared = newAlerts.Where(a => string.IsNullOrEmpty(a.Audience)).ToList();
        var targeted = newAlerts.Where(a => !string.IsNullOrEmpty(a.Audience)).ToList();
        lock (_lock)
        {
            _alerts.AddRange(shared);
            if (_alerts.Count > MaxKept)
                _alerts.RemoveRange(0, _alerts.Count - MaxKept);
            _targeted.AddRange(targeted);
            if (_targeted.Count > MaxKeptTargeted)
                _targeted.RemoveRange(0, _targeted.Count - MaxKeptTargeted);
        }
        if (shared.Count > 0) OnChange?.Invoke(shared);
        foreach (var group in targeted.GroupBy(a => a.Audience!, StringComparer.OrdinalIgnoreCase))
            OnUserAlerts?.Invoke(group.Key, group.ToList());
    }

    /// <summary>Marks the shared alerts read (their read state is shared by everyone, as it always was).</summary>
    public void MarkAllRead()
    {
        lock (_lock)
        {
            foreach (var a in _alerts) a.Read = true;
        }
        OnChange?.Invoke(Array.Empty<Alert>());
    }

    /// <summary>Marks the shared alerts and this user's own alerts read.</summary>
    public void MarkAllReadFor(string? username)
    {
        lock (_lock)
        {
            foreach (var a in OwnLocked(username)) a.Read = true;
        }
        MarkAllRead();
        if (!string.IsNullOrEmpty(username)) OnUserAlerts?.Invoke(username, Array.Empty<Alert>());
    }

    private IEnumerable<Alert> OwnLocked(string? username) =>
        string.IsNullOrEmpty(username)
            ? Enumerable.Empty<Alert>()
            : _targeted.Where(a => string.Equals(a.Audience, username, StringComparison.OrdinalIgnoreCase)).ToList();
}
