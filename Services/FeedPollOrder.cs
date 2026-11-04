using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Decides the order sources are fetched in. The poll runs a few fetches at a time, and a source waiting out its
/// host's cooldown still holds one of those slots. Sources arrive sorted by category, so one host with dozens of
/// sources (Reddit: 37 subreddits, each queued behind a multi-second cooldown and often a 429 back-off) used to occupy
/// every slot and leave the News, Research and Tools sources unfetched for the better part of an hour.
/// Ordinary hosts go first; crowded hosts follow, interleaved so no single one monopolises the slots.
/// </summary>
public static class FeedPollOrder
{
    /// <summary>A host with more sources than this is "crowded".</summary>
    public const int CrowdedHostThreshold = 8;

    /// <summary>
    /// How many of a rate-limit-prone host's sources are fetched per poll. Reddit's anonymous RSS answered about half of all requests with
    /// HTTP 429 or a timeout when every subreddit was fetched in one burst, every poll. Fetching a few at a time and rotating through the
    /// rest keeps each burst small enough to be accepted, at the cost of each subreddit refreshing every (count / batch) polls: with 16
    /// subreddits, 4 per poll and a 15-minute poll, hourly. Other crowded hosts (YouTube) have not shown the problem and stay all-at-once.
    /// </summary>
    public const int RedditBatchSize = 4;

    /// <summary>Null means "fetch every source on this host each poll".</summary>
    public static int? BatchSizeFor(string? host) =>
        host is not null && host.EndsWith("reddit.com", StringComparison.OrdinalIgnoreCase) ? RedditBatchSize : null;

    /// <summary>
    /// Splits the enabled sources into those to fetch this poll and those to skip until their turn. <paramref name="round"/> is a counter
    /// that goes up by one per poll; a limited host's sources are taken in a window that slides by one batch per round and wraps, so every
    /// source comes up within ceil(count / batch) polls whatever the count. Sources of unlimited hosts are always due.
    /// </summary>
    public static (List<FeedSource> Due, List<FeedSource> Skipped) SelectForRound(IReadOnlyList<FeedSource> sources, int round)
    {
        var skipped = new HashSet<FeedSource>();
        foreach (var group in sources.GroupBy(s => FeedDomainThrottle.GetHost(s.Url) ?? ""))
        {
            if (BatchSizeFor(group.Key) is not { } batch) continue;
            var list = group.ToList();
            if (list.Count <= batch) continue;

            var start = (int)((long)Math.Max(0, round) * batch % list.Count);
            var due = Enumerable.Range(0, batch).Select(i => list[(start + i) % list.Count]).ToHashSet();
            foreach (var source in list.Where(s => !due.Contains(s))) skipped.Add(source);
        }

        return (sources.Where(s => !skipped.Contains(s)).ToList(), sources.Where(skipped.Contains).ToList());
    }

    public static List<FeedSource> Order(IReadOnlyList<FeedSource> sources)
    {
        var byHost = sources.GroupBy(s => FeedDomainThrottle.GetHost(s.Url) ?? "").ToDictionary(g => g.Key, g => g.Count());
        bool Crowded(FeedSource s) => byHost[FeedDomainThrottle.GetHost(s.Url) ?? ""] > CrowdedHostThreshold;

        var ordinary = sources.Where(s => !Crowded(s));
        var queues = sources.Where(Crowded)
            .GroupBy(s => FeedDomainThrottle.GetHost(s.Url) ?? "")
            .Select(g => new Queue<FeedSource>(g))
            .ToList();

        var result = new List<FeedSource>(ordinary);
        while (queues.Count > 0)
        {
            foreach (var queue in queues)
                result.Add(queue.Dequeue());
            queues.RemoveAll(q => q.Count == 0);
        }

        return result;
    }
}
