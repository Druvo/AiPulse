using AiPulse.Models;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Productivity;

/// <summary>
/// Background evaluation of saved-search alerts. It follows the FeedWatcherService pattern without needing any change there: the
/// watcher records each poll into <see cref="FeedHistoryService"/>, whose <c>Changed</c> event wakes this loop; a periodic timer is the
/// fallback (and covers WebSub pushes, which also go through the history). Each pass raises at most one in-app alert per rule (grouped),
/// capped per user and per pass, addressed to the rule's owner only, and optionally posted to that owner's own webhook.
/// </summary>
public sealed class SavedSearchAlertService : BackgroundService
{
    private readonly SavedSearchService _searches;
    private readonly FeedHistoryService _history;
    private readonly NotificationService _notify;
    private readonly WebhookService _webhooks;
    private readonly UserService _users;
    private readonly IWebHostEnvironment _env;
    private readonly SavedSearchAlertOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SavedSearchAlertService> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public SavedSearchAlertService(SavedSearchService searches, FeedHistoryService history, NotificationService notify, WebhookService webhooks,
        UserService users, IWebHostEnvironment env, IOptions<SavedSearchAlertOptions> options, TimeProvider time, ILogger<SavedSearchAlertService> log)
    {
        _searches = searches;
        _history = history;
        _notify = notify;
        _webhooks = webhooks;
        _users = users;
        _env = env;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    /// <summary>Asks for a pass soon (coalesced: several requests before the pass runs give one pass). The one-line hook for FeedWatcherService, if ever wanted.</summary>
    public void RequestEvaluation()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _history.Changed += RequestEvaluation;
        try
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Saved-search alert pass failed"); }

                try { await _wake.WaitAsync(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                // Let a burst of history writes (one per source batch) settle into one pass.
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
            }
        }
        finally
        {
            _history.Changed -= RequestEvaluation;
        }
    }

    /// <summary>One pass: evaluate, notify in-app, then fan out to webhooks. Returns the alerts raised.</summary>
    public async Task<IReadOnlyList<Alert>> RunOnceAsync(CancellationToken ct = default)
    {
        var users = await _users.GetAllAsync();
        var usernames = users.Where(u => u.Status == UserStatuses.Approved).ToDictionary(u => u.Id, u => u.Username);
        if (usernames.Count == 0) return Array.Empty<Alert>();

        var results = await _searches.EvaluateAsync(_history.Items, usernames, _options, ct);
        if (results.Count == 0) return Array.Empty<Alert>();

        var now = _time.GetUtcNow();
        var alerts = results.Select(r => SavedSearchAlerting.ToAlert(r.Plan, r.Username, now)).ToList();
        _log.LogInformation("Saved searches raised {Count} alert(s)", alerts.Count);
        _notify.Add(alerts);

        for (var i = 0; i < results.Count; i++)
        {
            var (plan, username) = results[i];
            if (!plan.Rule.WebhookEnabled) continue;
            var url = ReadingStateService.GetWebhookUrlFor(_env, username);
            if (url is null) continue;
            // The webhook gets a real link, never the relative /saved one.
            var alert = alerts[i];
            var outgoing = new Alert { Title = alert.Title, Link = plan.Newest.Link, SourceName = plan.Newest.SourceName, Kind = alert.Kind, Details = alert.Details, Count = alert.Count };
            try { await _webhooks.SendAsync(url, outgoing, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Saved-search webhook delivery failed"); }
        }
        return alerts;
    }
}
