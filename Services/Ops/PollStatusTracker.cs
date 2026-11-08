using AiPulse.Models;

namespace AiPulse.Services.Ops;

/// <summary>What the operations console knows about the background feed poll. Every field is "since this process started".</summary>
public sealed record PollStatus
{
    /// <summary>Full polls seen finishing (background watcher polls plus any forced refresh from a page).</summary>
    public int PollsCompleted { get; init; }
    public DateTimeOffset? LastStartedAt { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    public TimeSpan? LastDuration { get; init; }
    /// <summary>Sources the last poll actually fetched (successes plus failures).</summary>
    public int LastFetched { get; init; }
    public int LastFailed { get; init; }
    /// <summary>Enabled sources the last poll left for a later round (the rotating rate-limit batches).</summary>
    public int LastSkipped { get; init; }
    public int LastItems { get; init; }
    public IReadOnlyList<string> LastErrors { get; init; } = Array.Empty<string>();
    /// <summary>True between the first source finishing and the result being published.</summary>
    public bool InProgress { get; init; }
    public int InProgressFetched { get; init; }
    /// <summary>The aggregator's own round counter, when it reports one (see <see cref="PollStatusTracker.ReportRound"/>); otherwise null.</summary>
    public int? ReportedRound { get; init; }
    public DateTimeOffset? LastWebSubMergeAt { get; init; }
    public int WebSubMerges { get; init; }
}

/// <summary>
/// Read-only bookkeeping about the feed poll for the admin operations console. It needs nothing from the aggregator beyond the two
/// public events it already raises: <see cref="FeedAggregatorService.PartialProgress"/> fires once per source as a poll runs, and
/// <see cref="FeedAggregatorService.CacheUpdated"/> fires when the poll's result is published (or a WebSub push is merged, which keeps the
/// old FetchedAt). From those it derives start/end, duration, sources fetched/failed/skipped and item counts.
/// The aggregator can optionally call <see cref="ReportRound"/> to give the exact rotation counters instead of the derived ones.
/// </summary>
public sealed class PollStatusTracker
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;

    private int _polls;
    private DateTimeOffset? _currentStart;
    private int _currentFetched;
    private DateTimeOffset? _lastStart;
    private DateTimeOffset? _lastEnd;
    private DateTimeOffset? _lastFetchedAt;
    private int _lastFetched;
    private int _lastFailed;
    private int _lastSkipped;
    private int _lastItems;
    private IReadOnlyList<string> _lastErrors = Array.Empty<string>();
    private int? _reportedRound;
    private int? _reportedDue;
    private int? _reportedSkipped;
    private DateTimeOffset? _lastMerge;
    private int _merges;
    private Func<int>? _enabledSourceCount;

    public PollStatusTracker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        StartedAt = _time.GetUtcNow();
    }

    /// <summary>When the tracker (and so, give or take a second, the app) started: the anchor for the watcher's poll schedule.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Subscribes to the aggregator's events. Safe to call once at startup.</summary>
    public void Attach(FeedAggregatorService feeds, Func<int> enabledSourceCount)
    {
        _enabledSourceCount = enabledSourceCount;
        feeds.PartialProgress += OnSourceFinished;
        feeds.CacheUpdated += OnResultPublished;
    }

    public void Detach(FeedAggregatorService feeds)
    {
        feeds.PartialProgress -= OnSourceFinished;
        feeds.CacheUpdated -= OnResultPublished;
    }

    /// <summary>Optional hook for FeedAggregatorService.FetchAllAsync: the exact round and how many sources it chose and skipped.</summary>
    public void ReportRound(int round, int due, int skipped)
    {
        lock (_lock)
        {
            _reportedRound = round;
            _reportedDue = due;
            _reportedSkipped = skipped;
            _currentStart ??= _time.GetUtcNow();
        }
    }

    /// <summary>Called once per source as a poll runs (from several threads at once).</summary>
    public void OnSourceFinished(IReadOnlyList<FeedItem> _)
    {
        lock (_lock)
        {
            _currentStart ??= _time.GetUtcNow();
            _currentFetched++;
        }
    }

    /// <summary>Called when the aggregator publishes a new cached result.</summary>
    public void OnResultPublished(FeedResult result)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();

            // A WebSub push merged into the cache keeps the poll's FetchedAt: it is not a poll.
            if (_lastFetchedAt is { } previous && previous == result.FetchedAt && _currentStart is null)
            {
                _merges++;
                _lastMerge = now;
                _lastItems = result.Items.Count;
                return;
            }

            _polls++;
            _lastStart = _currentStart ?? now;
            _lastEnd = now;
            _lastFetchedAt = result.FetchedAt;
            _lastFetched = _reportedDue ?? _currentFetched;
            _lastFailed = result.Errors.Count;
            _lastItems = result.Items.Count;
            _lastErrors = result.Errors.ToList();
            var enabled = _enabledSourceCount?.Invoke();
            _lastSkipped = _reportedSkipped ?? (enabled is { } n ? Math.Max(0, n - _lastFetched) : 0);

            _currentStart = null;
            _currentFetched = 0;
            _reportedDue = null;
            _reportedSkipped = null;
        }
    }

    public PollStatus Snapshot()
    {
        lock (_lock)
        {
            return new PollStatus
            {
                PollsCompleted = _polls,
                LastStartedAt = _lastStart,
                LastCompletedAt = _lastEnd,
                LastDuration = _lastStart is { } s && _lastEnd is { } e ? e - s : null,
                LastFetched = _lastFetched,
                LastFailed = _lastFailed,
                LastSkipped = _lastSkipped,
                LastItems = _lastItems,
                LastErrors = _lastErrors,
                InProgress = _currentStart is not null,
                InProgressFetched = _currentFetched,
                ReportedRound = _reportedRound,
                LastWebSubMergeAt = _lastMerge,
                WebSubMerges = _merges
            };
        }
    }

    /// <summary>
    /// The background watcher starts its timer about five seconds after startup and then ticks every <paramref name="interval"/>,
    /// whatever any page does in between, so its next tick can be worked out from the start time alone.
    /// </summary>
    public DateTimeOffset NextScheduledPoll(TimeSpan interval, TimeSpan? initialDelay = null)
    {
        var first = StartedAt + (initialDelay ?? TimeSpan.FromSeconds(5));
        var now = _time.GetUtcNow();
        if (now <= first || interval <= TimeSpan.Zero) return first;
        var ticks = (long)Math.Ceiling((now - first).Ticks / (double)interval.Ticks);
        return first + TimeSpan.FromTicks(interval.Ticks * ticks);
    }
}
