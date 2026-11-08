using System.Globalization;
using System.Text.RegularExpressions;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>How densely the News feed lays its cards out. Persisted per browser (see wwwroot/js/feed-prefs.js).</summary>
public enum FeedDensity
{
    /// <summary>One card per row, thumbnail on the left, summary and tags visible.</summary>
    Comfortable,
    /// <summary>Headline rows only: no summary, tags or type badges, so many more fit on a screen.</summary>
    Compact,
    /// <summary>Image-on-top cards in a responsive grid.</summary>
    Grid
}

public readonly record struct TextSegment(string Text, bool Match);

public sealed record DashboardSummary(int NewToday, int UnreadThisWeek, int Streak, string? TopTopic, int TopTopicCount);

public sealed record DayGroup(DateOnly Day, string Label, IReadOnlyList<FeedItem> Items);

/// <summary>
/// The presentation decisions behind the content pages (News, Dashboard, Topics, Releases, global search) as pure functions,
/// so they are testable without a browser, a circuit or a clock: density names, relative times, search highlighting and ranking,
/// the dashboard's summary numbers, heatmap shading, topic-cloud sizing and the release timeline's day grouping.
/// "Local" days are always taken in the offset of the <c>now</c> the caller passes (the server's local time in the app).
/// </summary>
public static class ContentUi
{
    /// <summary>The localStorage key and cookie name the chosen density is saved under.</summary>
    public const string DensityStorageKey = "aipulse-feed-density";

    /// <summary>
    /// Reads a saved or linked density. Accepts the current names and the News page's older <c>?view=List|Grid</c> values, case-insensitively;
    /// anything else (missing, tampered, from a future version) falls back rather than throwing.
    /// </summary>
    public static FeedDensity ParseDensity(string? value, FeedDensity fallback = FeedDensity.Grid) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "comfortable" or "list" => FeedDensity.Comfortable,
            "compact" => FeedDensity.Compact,
            "grid" => FeedDensity.Grid,
            _ => fallback
        };

    public static string DensityKey(FeedDensity density) => density.ToString().ToLowerInvariant();

    /// <summary>The <c>FeedItemCard.Layout</c> value each density renders with.</summary>
    public static string CardLayout(FeedDensity density) => density switch
    {
        FeedDensity.Grid => "Grid",
        FeedDensity.Compact => "Compact",
        _ => "List"
    };

    /// <summary>Short, scannable age for card meta lines ("just now", "5m ago", "3h ago", "Yesterday", "Mon", "Nov 3", "Nov 3, 2025"). The full timestamp goes in a title/datetime attribute.</summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToOffset(now.Offset);
        var age = now - local;
        if (age < TimeSpan.FromMinutes(1)) return "just now"; // also covers small clock skew into the future
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";

        var today = DateOnly.FromDateTime(now.DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        if (day == today) return $"{(int)age.TotalHours}h ago";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return local.ToString("ddd", CultureInfo.InvariantCulture);
        if (day.Year == today.Year) return local.ToString("MMM d", CultureInfo.InvariantCulture);
        return local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Splits <paramref name="text"/> into plain and matching runs for every whitespace-separated term of <paramref name="query"/>,
    /// case-insensitively. Overlapping or touching matches merge into one run, and the runs always concatenate back to the original text.
    /// </summary>
    public static IReadOnlyList<TextSegment> Highlight(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TextSegment>();
        var terms = Terms(query);
        if (terms.Length == 0) return new[] { new TextSegment(text, false) };

        var marked = new bool[text.Length];
        foreach (var term in terms)
        {
            var at = 0;
            while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                for (var i = at; i < at + term.Length; i++) marked[i] = true;
                at += term.Length;
            }
        }

        var segments = new List<TextSegment>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marked[i] != marked[start])
            {
                segments.Add(new TextSegment(text[start..i], marked[start]));
                start = i;
            }
        }
        return segments;
    }

    /// <summary>
    /// How well an entry matches a search, for ordering results within a group: every term must appear in the title or subtitle (0 = no match).
    /// Exact title beats a title prefix, beats a word start inside the title, beats anywhere in the title, beats subtitle only.
    /// </summary>
    public static int MatchScore(string title, string? subtitle, string? query)
    {
        var terms = Terms(query);
        if (terms.Length == 0) return 0;
        subtitle ??= "";

        var total = 0;
        foreach (var term in terms)
        {
            int score;
            if (string.Equals(title.Trim(), query!.Trim(), StringComparison.OrdinalIgnoreCase)) score = 100;
            else if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score = 80;
            else if (Regex.IsMatch(title, @"(^|[\s\-_/.(])" + Regex.Escape(term), RegexOptions.IgnoreCase)) score = 60;
            else if (title.Contains(term, StringComparison.OrdinalIgnoreCase)) score = 40;
            else if (subtitle.Contains(term, StringComparison.OrdinalIgnoreCase)) score = 15;
            else return 0;
            total += score;
        }
        return total / terms.Length;
    }

    /// <summary>
    /// Consecutive days of reading ending today. A day with nothing read yet doesn't break the streak until it is over: if today is empty,
    /// the run ending yesterday still counts (the dashboard shouldn't greet someone at 8 am with "0-day streak").
    /// </summary>
    public static int ReadingStreak(IEnumerable<DateOnly> readDays, DateOnly today)
    {
        var days = readDays.ToHashSet();
        var d = days.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (days.Contains(d)) { streak++; d = d.AddDays(-1); }
        return streak;
    }

    /// <summary>
    /// The dashboard's hero numbers: headlines published since local midnight, unread headlines from the last 7 days, the reading streak, and the
    /// week's most common topic. Release notes are excluded, matching what the News feed shows.
    /// </summary>
    public static DashboardSummary Summarize(IEnumerable<FeedItem> items, Func<string, bool> isRead, IEnumerable<DateTimeOffset> readTimes, DateTimeOffset now)
    {
        var headlines = items.Where(PublicFeeds.IsHeadline).ToList();
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var weekAgo = now.AddDays(-7);

        var week = headlines.Where(i => i.Published >= weekAgo).ToList();
        var top = PublicFeeds.Topics(week, weekAgo, take: 1, minCount: 1).FirstOrDefault();
        var today = DateOnly.FromDateTime(now.DateTime);
        var readDays = readTimes.Select(t => DateOnly.FromDateTime(t.ToOffset(now.Offset).DateTime));

        return new DashboardSummary(
            NewToday: headlines.Count(i => i.Published >= midnight && i.Published <= now.AddMinutes(5)),
            UnreadThisWeek: week.Count(i => !isRead(i.Link)),
            Streak: ReadingStreak(readDays, today),
            TopTopic: top?.Tag,
            TopTopicCount: top?.Count ?? 0);
    }

    /// <summary>
    /// Heatmap shade 0-4. Square-root scaling, so one unusually busy day (a backfill, a big launch) doesn't flatten every other day into the
    /// palest shade the way a straight ratio to the maximum did. Any non-zero day is at least level 1, so "something happened" is always visible.
    /// </summary>
    public static int HeatLevel(int count, int max)
    {
        if (count <= 0 || max <= 0) return 0;
        var ratio = Math.Sqrt(Math.Min(count, max) / (double)max);
        return ratio switch
        {
            <= 0.25 => 1,
            <= 0.5 => 2,
            <= 0.75 => 3,
            _ => 4
        };
    }

    /// <summary>Topic-cloud size tier 1-5 on a log scale, so the busiest topic stands out without making the long tail unreadably small.</summary>
    public static int TopicTier(int count, int max)
    {
        if (count <= 0 || max <= 1) return 1;
        var t = Math.Log(Math.Min(count, max)) / Math.Log(max);
        return 1 + (int)Math.Round(Math.Clamp(t, 0, 1) * 4);
    }

    /// <summary>
    /// Groups items (already in the order to show) by local publication day, newest day first, with human labels:
    /// "Today", "Yesterday", the weekday name within the last week, then the full date.
    /// </summary>
    public static IReadOnlyList<DayGroup> GroupByDay(IEnumerable<FeedItem> items, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        return items
            .GroupBy(i => DateOnly.FromDateTime(i.Published.ToOffset(now.Offset).DateTime))
            .OrderByDescending(g => g.Key)
            .Select(g => new DayGroup(g.Key, DayLabel(g.Key, today), g.ToList()))
            .ToList();
    }

    public static string DayLabel(DateOnly day, DateOnly today)
    {
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7) && day < today) return day.ToString("dddd", CultureInfo.InvariantCulture);
        return day.Year == today.Year
            ? day.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static readonly Regex VersionPattern = new(@"(?<![\w.])v?\d+(?:\.\d+)+(?:[-+][0-9A-Za-z][0-9A-Za-z.\-]*)?(?![\w])", RegexOptions.Compiled);

    /// <summary>
    /// Splits a release title such as "huggingface/transformers v4.46.0" or "Release 1.2.0 of ollama" into the project and the version tag,
    /// for the release timeline's version pill. Version is null when the title carries none.
    /// </summary>
    public static (string Project, string? Version) SplitRelease(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("", null);
        var m = VersionPattern.Match(title);
        if (!m.Success) return (title.Trim(), null);

        var project = (title[..m.Index] + title[(m.Index + m.Length)..]).Trim();
        project = Regex.Replace(project, @"\s{2,}", " ").Trim(' ', ':', '-', '–', '—', '·', '|').Trim();
        if (project.Length == 0) project = title.Trim();
        return (project, m.Value);
    }
}
