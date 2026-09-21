using System.Collections.Concurrent;

namespace AiPulse.Services;

/// <summary>Per-domain politeness throttle so the aggregator doesn't hammer any one feed host (e.g. Reddit
/// 429s when 100+ subreddit sources share the one host). Static/process-wide by design - the cooldown
/// state needs to be shared across every concurrent fetch, not per-request.</summary>
internal static class FeedDomainThrottle
{
    /// <summary>Tracks last fetch time per host to avoid rate-limiting.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> _lastDomainFetch = new();

    /// <summary>Gates the read-wait-write around <see cref="_lastDomainFetch"/> per host, so concurrent
    /// requests to the same host can't all read the same stale timestamp and fire together (a bare
    /// check-then-set is not atomic under concurrency, which is what actually let bursts of simultaneous
    /// Reddit requests through despite the "cooldown"). Capacity 1 for every host except reddit.com (see
    /// <see cref="RedditMaxConcurrency"/>) - reddit's 100+ sources sharing one host meant full
    /// serialization made it the poll's long pole, so it gets a couple of concurrent slots instead of one.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _domainLocks = new();

    private static readonly TimeSpan DomainCooldown = TimeSpan.FromSeconds(3);

    /// <summary>Reddit's anonymous/unauthenticated RSS rate limit is tighter than most other hosts, and this
    /// app can have 100+ subreddit sources sharing the one host - give it a longer, dedicated cooldown.</summary>
    private static readonly TimeSpan RedditCooldown = TimeSpan.FromSeconds(6);

    /// <summary>Reddit sources (100+ of them, one host) used to fully serialize behind a 1-at-a-time host
    /// lock, which meant they alone could dominate the poll's wall-clock time. Letting a couple through
    /// concurrently cuts that down while still keeping real spacing between requests via <see cref="RedditCooldown"/>.</summary>
    private const int RedditMaxConcurrency = 2;

    /// <summary>
    /// Reddit's responses (429 or not) carry real "x-ratelimit-remaining"/"x-ratelimit-reset" headers -
    /// verified live: a single request can already show remaining=0, reset=13s, i.e. its actual budget is
    /// tighter and more specific than any hardcoded guess. Learned per host from the most recent response
    /// and used instead of <see cref="RedditCooldown"/> once available, so every source sharing that host
    /// benefits from what the last one just learned rather than each guessing independently.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TimeSpan> _learnedCooldown = new();

    /// <summary>
    /// Atomically reserves the next cooldown slot for a URL's host: waits if another request to the same
    /// host fetched too recently, then records this attempt's time - all under a per-host lock, so two
    /// concurrent callers can't both read the same stale "last fetch" timestamp and slip through together.
    /// Called before every actual HTTP attempt (including 429 retries), not just once per source, so a
    /// source backing off from a rate limit doesn't leave the shared host's cooldown stale while sibling
    /// sources for the same host race past it.
    /// </summary>
    public static async Task WaitForSlotAsync(string? url, CancellationToken ct)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var host = uri.Host;
        var cooldown = _learnedCooldown.TryGetValue(host, out var learned) ? learned
            : host.EndsWith("reddit.com", StringComparison.OrdinalIgnoreCase) ? RedditCooldown
            : DomainCooldown;

        var hostLock = _domainLocks.GetOrAdd(host, h =>
            new SemaphoreSlim(h.EndsWith("reddit.com", StringComparison.OrdinalIgnoreCase) ? RedditMaxConcurrency : 1));
        await hostLock.WaitAsync(ct);
        try
        {
            if (_lastDomainFetch.TryGetValue(host, out var last))
            {
                var elapsed = DateTime.UtcNow - last;
                if (elapsed < cooldown)
                    await Task.Delay(cooldown - elapsed, ct);
            }
            _lastDomainFetch[host] = DateTime.UtcNow;
        }
        finally
        {
            hostLock.Release();
        }
    }

    public static string? GetHost(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Reads "x-ratelimit-reset" (seconds until the host's own rate-limit window refills) off a response,
    /// when present, and uses it as that host's cooldown going forward - clamped to a sane range since it's
    /// an unofficial header and shouldn't be trusted blindly (e.g. a rogue 0 or an absurdly large value).
    /// </summary>
    public static void LearnCooldownFromResponse(string host, HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("x-ratelimit-reset", out var values)) return;
        if (!double.TryParse(values.FirstOrDefault(), out var resetSeconds) || resetSeconds <= 0) return;

        _learnedCooldown[host] = TimeSpan.FromSeconds(Math.Clamp(resetSeconds, 2, 60));
    }

    public static TimeSpan? GetLearnedCooldown(string? host) =>
        host is not null && _learnedCooldown.TryGetValue(host, out var v) ? v : null;
}
