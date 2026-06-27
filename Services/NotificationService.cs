using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// In-memory hub for alerts about big releases and watchlist hits. The background
/// <see cref="FeedWatcherService"/> pushes alerts here; the UI bell subscribes to <see cref="OnChange"/>.
/// </summary>
public sealed class NotificationService
{
    private const int MaxKept = 100;
    private readonly object _lock = new();
    private readonly List<Alert> _alerts = new();

    /// <summary>Raised when new alerts are added. Args: the newly added alerts.</summary>
    public event Action<IReadOnlyList<Alert>>? OnChange;

    public IReadOnlyList<Alert> Recent
    {
        get { lock (_lock) return _alerts.OrderByDescending(a => a.CreatedAt).Take(30).ToList(); }
    }

    public int UnreadCount
    {
        get { lock (_lock) return _alerts.Count(a => !a.Read); }
    }

    public void Add(IReadOnlyList<Alert> newAlerts)
    {
        if (newAlerts.Count == 0) return;
        lock (_lock)
        {
            _alerts.AddRange(newAlerts);
            if (_alerts.Count > MaxKept)
                _alerts.RemoveRange(0, _alerts.Count - MaxKept);
        }
        OnChange?.Invoke(newAlerts);
    }

    public void MarkAllRead()
    {
        lock (_lock)
        {
            foreach (var a in _alerts) a.Read = true;
        }
        OnChange?.Invoke(Array.Empty<Alert>());
    }
}
