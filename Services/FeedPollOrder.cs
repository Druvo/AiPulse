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
