using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services.Productivity;

/// <summary>
/// Saved searches and their alert bookkeeping. Every method takes the owner's user id explicitly (same rule as FollowService), so a
/// rule can only ever be read or changed by the user who owns it.
/// </summary>
public sealed class SavedSearchService
{
    public const int MaxRulesPerUser = 50;
    public const int MaxNameLength = 80;

    private readonly ProductivityDb _db;
    private readonly TimeProvider _time;

    public SavedSearchService(ProductivityDb db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<List<SavedSearchRule>> ListAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rules = await db.SavedSearchRules.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        return rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
    }

    public async Task<int> CountAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        return await db.SavedSearchRules.CountAsync(r => r.UserId == userId, ct);
    }

    /// <summary>Validates the user-editable fields. Returns an error message, or null when the rule can be saved.</summary>
    public static string? Validate(SavedSearchRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return "Give the search a name.";
        if (rule.Name.Trim().Length > MaxNameLength) return $"Keep the name under {MaxNameLength} characters.";
        if ((rule.Keywords ?? "").Length > SavedSearchQuery.MaxKeywordsLength) return $"Keep the keywords under {SavedSearchQuery.MaxKeywordsLength} characters.";
        var query = SavedSearchQuery.From(rule);
        if (!query.HasPositiveCriterion) return "Add at least one keyword, tag or source (exclusions alone would match almost everything).";
        return null;
    }

    /// <summary>
    /// Creates (Id 0) or updates the user's rule. Switching alerts on records every item that already matches as "seen", so the user
    /// is told about what arrives from now on rather than flooded with the backlog. Returns the saved rule, or an error.
    /// </summary>
    public async Task<(SavedSearchRule? Rule, string? Error)> SaveAsync(int userId, SavedSearchRule input, IReadOnlyList<FeedItem> currentItems, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null) return (null, error);

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _db.OpenAsync(ct);

        SavedSearchRule rule;
        var alertsTurnedOn = false;
        if (input.Id == 0)
        {
            if (await db.SavedSearchRules.CountAsync(r => r.UserId == userId, ct) >= MaxRulesPerUser)
                return (null, $"You can keep up to {MaxRulesPerUser} saved searches. Delete one first.");
            rule = new SavedSearchRule { UserId = userId, CreatedUtc = now };
            db.SavedSearchRules.Add(rule);
            alertsTurnedOn = input.AlertEnabled;
        }
        else
        {
            var existing = await db.SavedSearchRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.UserId == userId, ct);
            if (existing is null) return (null, "That saved search no longer exists.");
            rule = existing;
            alertsTurnedOn = input.AlertEnabled && !existing.AlertEnabled;
        }

        var queryChanged = rule.Keywords != (input.Keywords ?? "").Trim() || rule.Tags != Normalise(input.Tags, ',') || rule.Sources != Normalise(input.Sources, '\n');
        rule.Name = input.Name.Trim();
        rule.Keywords = (input.Keywords ?? "").Trim();
        rule.Tags = Normalise(input.Tags, ',');
        rule.Sources = Normalise(input.Sources, '\n');
        rule.AlertEnabled = input.AlertEnabled;
        rule.WebhookEnabled = input.WebhookEnabled;
        rule.UpdatedUtc = now;
        if (alertsTurnedOn) rule.AlertsSinceUtc = now;
        await db.SaveChangesAsync(ct);

        // A changed query also gets a fresh baseline, or editing "agents" into "AI" would alert on every older AI story at once.
        if (rule.AlertEnabled && (alertsTurnedOn || queryChanged))
            await SeedBaselineAsync(db, rule, currentItems, now, ct);

        return (rule, null);
    }

    public async Task<bool> SetAlertAsync(int userId, int ruleId, bool enabled, IReadOnlyList<FeedItem> currentItems, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rule = await db.SavedSearchRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId, ct);
        if (rule is null) return false;
        if (rule.AlertEnabled == enabled) return true;
        var now = _time.GetUtcNow().UtcDateTime;
        rule.AlertEnabled = enabled;
        rule.UpdatedUtc = now;
        if (enabled) rule.AlertsSinceUtc = now;
        await db.SaveChangesAsync(ct);
        if (enabled) await SeedBaselineAsync(db, rule, currentItems, now, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(int userId, int ruleId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rule = await db.SavedSearchRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId, ct);
        if (rule is null) return false;
        db.SavedSearchRules.Remove(rule);
        db.SavedSearchHits.RemoveRange(db.SavedSearchHits.Where(h => h.RuleId == ruleId));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The user's most recent alerted matches (persisted, unlike the in-memory bell), newest first.</summary>
    public async Task<List<SavedSearchHit>> RecentAlertsAsync(int userId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        return await db.SavedSearchHits.AsNoTracking()
            .Where(h => h.UserId == userId && h.Notified)
            .OrderByDescending(h => h.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    private static async Task SeedBaselineAsync(ProductivityDbContext db, SavedSearchRule rule, IReadOnlyList<FeedItem> items, DateTime now, CancellationToken ct)
    {
        var query = SavedSearchQuery.From(rule);
        var existing = (await db.SavedSearchHits.Where(h => h.RuleId == rule.Id).Select(h => h.LinkHash).ToListAsync(ct)).ToHashSet();
        foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.Link) && query.Matches(i)))
        {
            var hash = SavedSearchQuery.LinkHash(item.Link);
            if (!existing.Add(hash)) continue;
            db.SavedSearchHits.Add(Hit(rule, item, hash, notified: false, now));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One evaluation pass over every user's alerting rules: plans (dedupe + caps), records the handled matches, and returns what to
    /// notify. <paramref name="usernames"/> maps user id to username for approved users; rules of anyone else are skipped.
    /// </summary>
    public async Task<IReadOnlyList<(RuleAlertPlan Plan, string Username)>> EvaluateAsync(
        IReadOnlyList<FeedItem> items, IReadOnlyDictionary<int, string> usernames, SavedSearchAlertOptions options, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _db.OpenAsync(ct);
        var rules = await db.SavedSearchRules.Where(r => r.AlertEnabled).ToListAsync(ct);
        rules = rules.Where(r => usernames.ContainsKey(r.UserId)).ToList();
        if (rules.Count == 0) return Array.Empty<(RuleAlertPlan, string)>();

        var ruleIds = rules.Select(r => r.Id).ToList();
        var handled = (await db.SavedSearchHits.Where(h => ruleIds.Contains(h.RuleId)).Select(h => new { h.RuleId, h.LinkHash }).ToListAsync(ct))
            .GroupBy(h => h.RuleId)
            .ToDictionary(g => g.Key, g => (ISet<string>)g.Select(h => h.LinkHash).ToHashSet());
        var empty = new HashSet<string>();

        var plans = SavedSearchAlerting.Plan(rules, items, id => handled.TryGetValue(id, out var set) ? set : empty, now, options);
        foreach (var plan in plans)
        {
            foreach (var item in plan.NewMatches)
                db.SavedSearchHits.Add(Hit(plan.Rule, item, SavedSearchQuery.LinkHash(item.Link), notified: true, now));
            plan.Rule.LastAlertUtc = now;
        }

        // Rows for items that have aged out of the feed history are no longer needed for dedupe.
        var cutoff = now.AddDays(-100);
        db.SavedSearchHits.RemoveRange(db.SavedSearchHits.Where(h => h.CreatedUtc < cutoff));
        await db.SaveChangesAsync(ct);

        return plans.Select(p => (p, usernames[p.Rule.UserId])).ToList();
    }

    private static SavedSearchHit Hit(SavedSearchRule rule, FeedItem item, string hash, bool notified, DateTime now) => new()
    {
        RuleId = rule.Id,
        UserId = rule.UserId,
        LinkHash = hash,
        Link = item.Link,
        Title = item.Title.Length > 300 ? item.Title[..300] : item.Title,
        SourceName = item.SourceName,
        Notified = notified,
        CreatedUtc = now
    };

    private static string Normalise(string? value, char separator) =>
        string.Join(separator == '\n' ? "\n" : ", ", SavedSearchQuery.SplitList(value?.Replace("\r", ""), separator).Distinct(StringComparer.OrdinalIgnoreCase));
}
