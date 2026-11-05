using AiPulse.Models;

namespace AiPulse.Services.Productivity;

/// <summary>Limits for one evaluation pass. Bound from "SavedSearchAlerts" in appsettings (all optional).</summary>
public sealed class SavedSearchAlertOptions
{
    /// <summary>Only items published within this many days can alert; older ones are history, not news.</summary>
    public int WindowDays { get; set; } = 7;
    /// <summary>Items published more than this long before alerts were switched on never alert (the baseline also covers them).</summary>
    public int GraceHoursBeforeEnable { get; set; } = 48;
    /// <summary>Alerts (one per rule, grouping its new matches) a single user can get from one pass.</summary>
    public int MaxAlertsPerUserPerPoll { get; set; } = 5;
    /// <summary>Alerts across all users from one pass. Rules over the cap are deferred to the next pass, not dropped.</summary>
    public int MaxAlertsPerPoll { get; set; } = 25;
    /// <summary>New matches recorded per rule per pass; the rest are picked up by the next pass.</summary>
    public int MaxMatchesPerRulePerPoll { get; set; } = 100;
    /// <summary>How often to evaluate when no new feed items have arrived to trigger a pass.</summary>
    public int IntervalMinutes { get; set; } = 15;
}

/// <summary>What one pass decided for one rule: the items to record as handled, and whether to raise an alert for them.</summary>
public sealed record RuleAlertPlan(SavedSearchRule Rule, IReadOnlyList<FeedItem> NewMatches)
{
    public FeedItem Newest => NewMatches[0];
}

/// <summary>
/// The pure part of saved-search alerting: given the rules, the recent items and what each rule has already handled, decide which
/// rules alert now. Dedupe is by (rule, link) so an item alerts a rule once however long it stays in the feed; a rule's new matches
/// are grouped into one alert; per-user and global caps defer the overflow to the next pass (least-recently-alerted rules go first,
/// so a noisy rule cannot starve the others).
/// </summary>
public static class SavedSearchAlerting
{
    public static IReadOnlyList<RuleAlertPlan> Plan(
        IEnumerable<SavedSearchRule> rules,
        IReadOnlyList<FeedItem> items,
        Func<int, ISet<string>> handledLinkHashes,
        DateTime nowUtc,
        SavedSearchAlertOptions options)
    {
        var plans = new List<RuleAlertPlan>();
        var perUser = new Dictionary<int, int>();
        var windowStart = nowUtc.AddDays(-Math.Max(1, options.WindowDays));

        var ordered = rules
            .Where(r => r.AlertEnabled)
            .OrderBy(r => r.LastAlertUtc ?? DateTime.MinValue)
            .ThenBy(r => r.Id);

        foreach (var rule in ordered)
        {
            if (plans.Count >= options.MaxAlertsPerPoll) break;
            if (perUser.GetValueOrDefault(rule.UserId) >= options.MaxAlertsPerUserPerPoll) continue;

            var query = SavedSearchQuery.From(rule);
            if (!query.HasPositiveCriterion) continue;

            var earliest = windowStart;
            if (rule.AlertsSinceUtc is { } since && since.AddHours(-options.GraceHoursBeforeEnable) > earliest)
                earliest = since.AddHours(-options.GraceHoursBeforeEnable);

            var handled = handledLinkHashes(rule.Id);
            var seen = new HashSet<string>();
            var matches = items
                .Where(i => !string.IsNullOrEmpty(i.Link) && i.Published.UtcDateTime >= earliest && query.Matches(i))
                .Where(i => seen.Add(i.Link) && !handled.Contains(SavedSearchQuery.LinkHash(i.Link)))
                .OrderByDescending(i => i.Published)
                .ThenBy(i => i.Link, StringComparer.Ordinal)
                .Take(Math.Max(1, options.MaxMatchesPerRulePerPoll))
                .ToList();
            if (matches.Count == 0) continue;

            plans.Add(new RuleAlertPlan(rule, matches));
            perUser[rule.UserId] = perUser.GetValueOrDefault(rule.UserId) + 1;
        }

        return plans;
    }

    /// <summary>The in-app alert for one plan: a single item links straight to it; several link to the rule on /saved.</summary>
    public static Alert ToAlert(RuleAlertPlan plan, string username, DateTimeOffset now)
    {
        var newest = plan.Newest;
        var many = plan.NewMatches.Count > 1;
        return new Alert
        {
            Title = $"Saved search: {plan.Rule.Name}",
            Link = many ? $"/saved#rule-{plan.Rule.Id}" : newest.Link,
            SourceName = many ? $"{plan.NewMatches.Count} new matches" : newest.SourceName,
            Kind = "Search",
            Details = newest.Title,
            Count = plan.NewMatches.Count,
            CreatedAt = now,
            Audience = username
        };
    }
}
