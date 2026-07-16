using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Options bound from the "Notifications" section of appsettings.json.</summary>
public sealed class NotificationOptions
{
    public int PollMinutes { get; set; } = 15;
    public bool NotifyReleases { get; set; } = true;
    public bool NotifyWatchlist { get; set; } = true;
    public bool NotifyTrends { get; set; } = true;
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
    private readonly WebhookService _webhooks;
    private readonly NotificationOptions _opt;
    private readonly ILogger<FeedWatcherService> _log;
    private bool _seeded;

    private static readonly TimeSpan ReleaseThrottleWindow = TimeSpan.FromHours(1);
    private readonly Dictionary<string, DateTimeOffset> _lastReleaseAlertBySource = new();

    // A tag needs at least this many items today to be worth flagging at all - stops a rare tag going 1->4
    // from counting as a "spike" just because 4/1 clears the multiplier below.
    private const int MinSpikeCount = 8;
    // ...and today's count needs to be at least this multiple of its recent daily average.
    private const double SpikeMultiplier = 3.0;
    private readonly Dictionary<string, DateOnly> _lastTrendAlertDate = new();

    public FeedWatcherService(
        FeedAggregatorService feeds,
        IWebHostEnvironment env,
        NotificationService notify,
        FeedHistoryService history,
        KnowledgeBaseService kb,
        WebSubService webSub,
        WebhookService webhooks,
        Microsoft.Extensions.Options.IOptions<NotificationOptions> opt,
        ILogger<FeedWatcherService> log)
    {
        _feeds = feeds;
        _env = env;
        _notify = notify;
        _history = history;
        _kb = kb;
        _webSub = webSub;
        _webhooks = webhooks;
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

        // Snapshot which links FeedHistoryService already knew about *before* recording this batch.
        // Its history is persisted to disk and loaded on startup, so this survives restarts - unlike the
        // old in-memory-only "seen" set, which forgot everything on every restart and silently re-seeded
        // instead of alerting, even for items that had genuinely arrived while the app was down.
        var alreadyKnown = _history.Items.Select(i => i.Link).ToHashSet();
        _history.Record(result.Items);

        try { await _webSub.RenewExpiringAsync(_kb.Sources, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "WebSub renewal pass failed"); }

        // On the very first poll of a brand-new install (no persisted history yet), just seed silently -
        // otherwise every item currently live in every feed would fire as a fresh alert. A restart with
        // existing history falls straight through and alerts on anything not already known.
        if (!_seeded)
        {
            _seeded = true;
            if (alreadyKnown.Count == 0)
            {
                _log.LogInformation("Feed watcher seeded with {Count} item(s) (first run, nothing to alert on)", result.Items.Count);
                return;
            }
            _log.LogInformation("Feed watcher resuming with {Count} previously-known item(s) from persisted history", alreadyKnown.Count);
        }

        var newAlerts = new List<Alert>();
        // Union of every user's watchlist - background alerts aren't scoped to one user (see the
        // GetAllUsersWatchlist doc comment for why this differs from the per-user News-page highlighting).
        var watchlist = _opt.NotifyWatchlist ? ReadingStateService.GetAllUsersWatchlist(_env) : Array.Empty<string>();
        var newReleasesBySource = new Dictionary<string, List<FeedItem>>();

        foreach (var item in result.Items)
        {
            if (string.IsNullOrEmpty(item.Link) || !alreadyKnown.Add(item.Link))
                continue; // already seen (or a duplicate link within this same batch)

            var text = item.Title + " " + item.Summary;
            var match = watchlist.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (newAlerts.Count < _opt.MaxPerPoll)
                    newAlerts.Add(new Alert { Title = item.Title, Link = item.Link, SourceName = item.SourceName, Kind = "Watchlist" });
            }
            else if (_opt.NotifyReleases && item.ContentType == "Release")
            {
                // Grouped below instead of alerting per-item - a source rotating in several "new" releases in
                // one poll (e.g. the GitHub Trending scrapes) would otherwise flood the bell with one alert each.
                if (!newReleasesBySource.TryGetValue(item.SourceName, out var bucket))
                    newReleasesBySource[item.SourceName] = bucket = new List<FeedItem>();
                bucket.Add(item);
            }
        }

        // One alert per source per poll, and only if that source hasn't already alerted within the last hour -
        // "same repo notification should show once in an hour" instead of once per new release item.
        foreach (var (sourceName, items) in newReleasesBySource)
        {
            if (newAlerts.Count >= _opt.MaxPerPoll) break;

            if (_lastReleaseAlertBySource.TryGetValue(sourceName, out var last) && DateTimeOffset.Now - last < ReleaseThrottleWindow)
                continue; // already notified about this source recently - items are still recorded as known above, just not re-alerted

            var newest = items.OrderByDescending(i => i.Published).First();
            newAlerts.Add(new Alert
            {
                Title = sourceName,
                Link = newest.Link,
                SourceName = sourceName,
                Kind = "Release",
                Details = newest.Title,
                Count = items.Count
            });
            _lastReleaseAlertBySource[sourceName] = DateTimeOffset.Now;
        }

        if (_opt.NotifyTrends && newAlerts.Count < _opt.MaxPerPoll)
            newAlerts.AddRange(DetectTrendingSpikes().Take(_opt.MaxPerPoll - newAlerts.Count));

        if (newAlerts.Count > 0)
        {
            _log.LogInformation("Feed watcher raising {Count} alert(s)", newAlerts.Count);
            _notify.Add(newAlerts);
            await FanOutWebhooksAsync(newAlerts, ct);
        }
    }

    /// <summary>
    /// Flags a tag whose item count today is a real spike against its own recent history - e.g. "Agent"
    /// going from ~50/day to 500 in one day. Plain arithmetic on FeedHistoryService's already-persisted
    /// items (no AI): group by (tag, day), compare today's count to the average of the last 7 days.
    /// Requires at least 3 of those days to have any activity at all, so a tag's very first day of
    /// existence doesn't read as an infinite "spike" against a baseline of zero. Throttled to one alert per
    /// tag per calendar day.
    /// </summary>
    private List<Alert> DetectTrendingSpikes()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var countsByTagAndDay = _history.Items
            .Where(i => i.Tags.Length > 0)
            .SelectMany(i => i.Tags.Select(t => (Tag: t, Day: DateOnly.FromDateTime(i.Published.LocalDateTime.Date))))
            .GroupBy(x => (x.Tag, x.Day), TagDayComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Count(), TagDayComparer.Instance);

        var alerts = new List<Alert>();
        var allTags = _history.Items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in allTags)
        {
            if (_lastTrendAlertDate.TryGetValue(tag, out var lastAlerted) && lastAlerted == today)
                continue; // already flagged this tag today

            var todayCount = countsByTagAndDay.GetValueOrDefault((tag, today));
            if (todayCount < MinSpikeCount) continue;

            var recentDays = Enumerable.Range(1, 7)
                .Select(n => countsByTagAndDay.GetValueOrDefault((tag, today.AddDays(-n))))
                .ToList();
            if (recentDays.Count(c => c > 0) < 3) continue; // not enough history to call this a spike yet

            var baseline = recentDays.Average();
            if (baseline <= 0 || todayCount < baseline * SpikeMultiplier) continue;

            _lastTrendAlertDate[tag] = today;
            alerts.Add(new Alert
            {
                Title = tag,
                Link = $"/news?tag={Uri.EscapeDataString(tag)}",
                SourceName = "Trending",
                Kind = "Trend",
                // Count is left at its default (1) rather than todayCount - NotificationBell renders it as
                // "(+N more)", grouped-alert phrasing that doesn't fit a plain "here's today's raw total"
                // number. The actual figures already live in Details.
                Details = $"{todayCount} items today vs ~{baseline:0.#}/day recently"
            });
        }

        return alerts;
    }

    /// <summary>Value-tuple keys with a string component need an explicit case-insensitive comparer, or "MCP" and "mcp" become different dictionary entries.</summary>
    private sealed class TagDayComparer : IEqualityComparer<(string Tag, DateOnly Day)>
    {
        public static readonly TagDayComparer Instance = new();
        public bool Equals((string Tag, DateOnly Day) x, (string Tag, DateOnly Day) y) =>
            x.Day == y.Day && string.Equals(x.Tag, y.Tag, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Tag, DateOnly Day) obj) =>
            HashCode.Combine(obj.Tag.ToLowerInvariant(), obj.Day);
    }

    /// <summary>
    /// Posts each new alert to every matching webhook route. A route with no keywords is a catch-all
    /// (matches everything, including every user's plain single webhook URL); a route with keywords only
    /// fires when the alert's title/details/source contains one of them - lets different topics (e.g. "MCP")
    /// go to a different channel than everything else. Best-effort - a failed webhook never breaks the poll.
    /// </summary>
    private async Task FanOutWebhooksAsync(List<Alert> alerts, CancellationToken ct)
    {
        var routes = ReadingStateService.GetAllWebhookRoutes(_env);
        if (routes.Count == 0) return;

        foreach (var alert in alerts)
        {
            var haystack = $"{alert.Title} {alert.Details} {alert.SourceName}";
            foreach (var route in routes)
            {
                var matches = route.Keywords.Count == 0 ||
                    route.Keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;

                try { await _webhooks.SendAsync(route.Url, alert, ct); }
                catch (Exception ex) { _log.LogDebug(ex, "Webhook delivery failed"); }
            }
        }
    }
}
