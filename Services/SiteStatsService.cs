using AiPulse.Models;
using Microsoft.Extensions.Options;

namespace AiPulse.Services;

public sealed record SiteStats(
    int ActiveSources,
    int GlossaryTerms,
    int LearningModules,
    int ItemsLast24Hours,
    DateTimeOffset? NewestItem,
    DateTimeOffset? LastPolled,
    int PollMinutes);

/// <summary>
/// The numbers public pages quote ("79 sources", "17 modules", "42 new items today"), counted from the live site instead of typed into
/// markup. Several pages once claimed 39, 51 and 202 sources while the real figure was different on every one of them. Cached briefly so a
/// busy landing page never re-sorts the whole saved history per visitor.
/// </summary>
public sealed class SiteStatsService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly KnowledgeBaseService _kb;
    private readonly FeedHistoryService _history;
    private readonly FeedAggregatorService _feeds;
    private readonly int _pollMinutes;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private (SiteStats Stats, DateTimeOffset At)? _cached;

    public SiteStatsService(KnowledgeBaseService kb, FeedHistoryService history, FeedAggregatorService feeds,
        IOptions<NotificationOptions> notifications, TimeProvider? time = null)
    {
        _kb = kb;
        _history = history;
        _feeds = feeds;
        _pollMinutes = Math.Max(1, notifications.Value.PollMinutes);
        _time = time ?? TimeProvider.System;
    }

    public SiteStats Current
    {
        get
        {
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                if (_cached is { } cached && now - cached.At < Ttl) return cached.Stats;

                var stats = Compute(
                    _kb.Sources.Count(s => s.Enabled), _kb.Glossary.Count, _kb.Learning.Count,
                    _history.Items, now, _feeds.LastFetched, _pollMinutes);
                _cached = (stats, now);
                return stats;
            }
        }
    }

    /// <summary>Pure so it can be tested without a database or a clock.</summary>
    public static SiteStats Compute(int activeSources, int glossaryTerms, int learningModules,
        IEnumerable<FeedItem> items, DateTimeOffset now, DateTimeOffset? lastPolled, int pollMinutes)
    {
        var since = now - TimeSpan.FromHours(24);
        var count = 0;
        DateTimeOffset? newest = null;

        // Release items are shown on Explore, not in the News Feed, so they are not "new headlines".
        foreach (var item in items.Where(i => i.ContentType != "Release"))
        {
            // A feed with a bad clock can publish "tomorrow"; that is not news from the last day.
            if (item.Published > now) continue;
            if (item.Published > since) count++;
            if (newest is null || item.Published > newest) newest = item.Published;
        }

        return new SiteStats(activeSources, glossaryTerms, learningModules, count, newest, lastPolled, pollMinutes);
    }

    /// <summary>"3 minutes ago", "2 hours ago": short and human, for a "last updated" line.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return Plural((int)span.TotalMinutes, "minute");
        if (span < TimeSpan.FromDays(1)) return Plural((int)span.TotalHours, "hour");
        return Plural((int)span.TotalDays, "day");

        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }
}
