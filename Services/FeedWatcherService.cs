using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Options bound from the "Notifications" section of appsettings.json.</summary>
public sealed class NotificationOptions
{
    public int PollMinutes { get; set; } = 15;
    public bool NotifyReleases { get; set; } = true;
    public bool NotifyWatchlist { get; set; } = true;
    /// <summary>Max alerts created in a single poll, to avoid floods.</summary>
    public int MaxPerPoll { get; set; } = 15;
}

/// <summary>
/// Background loop that periodically refreshes feeds and raises alerts for big releases
/// (new items with ContentType == "Release") and watchlist keyword hits.
/// </summary>
public sealed class FeedWatcherService : BackgroundService
{
    private readonly FeedAggregatorService _feeds;
    private readonly IWebHostEnvironment _env;
    private readonly NotificationService _notify;
    private readonly FeedHistoryService _history;
    private readonly KnowledgeBaseService _kb;
    private readonly WebSubService _webSub;
    private readonly NotificationOptions _opt;
    private readonly ILogger<FeedWatcherService> _log;
    private readonly HashSet<string> _seen = new();
    private bool _seeded;

    public FeedWatcherService(
        FeedAggregatorService feeds,
        IWebHostEnvironment env,
        NotificationService notify,
        FeedHistoryService history,
        KnowledgeBaseService kb,
        WebSubService webSub,
        Microsoft.Extensions.Options.IOptions<NotificationOptions> opt,
        ILogger<FeedWatcherService> log)
    {
        _feeds = feeds;
        _env = env;
        _notify = notify;
        _history = history;
        _kb = kb;
        _webSub = webSub;
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup isn't blocked on network.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { return; }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _opt.PollMinutes));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Feed watcher poll failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var result = await _feeds.GetAsync(force: true, ct);
        _history.Record(result.Items);

        try { await _webSub.RenewExpiringAsync(_kb.Sources, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "WebSub renewal pass failed"); }

        // First pass just records what's already there so we don't alert on the whole backlog.
        if (!_seeded)
        {
            foreach (var item in result.Items) _seen.Add(item.Link);
            _seeded = true;
            _log.LogInformation("Feed watcher seeded with {Count} items", _seen.Count);
            return;
        }

        var newAlerts = new List<Alert>();
        // Union of every user's watchlist - background alerts aren't scoped to one user (see the
        // GetAllUsersWatchlist doc comment for why this differs from the per-user News-page highlighting).
        var watchlist = _opt.NotifyWatchlist ? ReadingStateService.GetAllUsersWatchlist(_env) : Array.Empty<string>();

        foreach (var item in result.Items)
        {
            if (string.IsNullOrEmpty(item.Link) || !_seen.Add(item.Link))
                continue; // already seen

            if (newAlerts.Count >= _opt.MaxPerPoll)
                continue;

            var text = item.Title + " " + item.Summary;
            var match = watchlist.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                newAlerts.Add(new Alert { Title = item.Title, Link = item.Link, SourceName = item.SourceName, Kind = "Watchlist" });
            }
            else if (_opt.NotifyReleases && item.ContentType == "Release")
            {
                newAlerts.Add(new Alert { Title = item.Title, Link = item.Link, SourceName = item.SourceName, Kind = "Release" });
            }
        }

        if (newAlerts.Count > 0)
        {
            _log.LogInformation("Feed watcher raising {Count} alert(s)", newAlerts.Count);
            _notify.Add(newAlerts);
        }
    }
}
