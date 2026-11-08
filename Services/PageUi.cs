using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Pure presentation logic for the learning, tools and admin pages (Learn, Glossary, Tools, Ask, Settings, Sources,
/// Source Health, Users, Reading Stats). Kept out of the .razor files so it can be unit tested without rendering anything.
/// </summary>
public static class GlossaryIndex
{
    /// <summary>The A-Z rail, with '#' first for terms that start with a digit or symbol.</summary>
    public static readonly IReadOnlyList<char> Alphabet = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    /// <summary>URL-fragment-safe id for a term, so /glossary#retrieval-augmented-generation-rag links straight to its card.</summary>
    public static string Slug(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return "term";
        var sb = new StringBuilder(term.Length);
        var lastDash = true;
        foreach (var ch in term.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "term" : slug;
    }

    /// <summary>The index letter a term files under: its first A-Z letter, or '#' for digits/symbols.</summary>
    public static char LetterOf(string term)
    {
        var t = (term ?? "").TrimStart();
        if (t.Length == 0) return '#';
        var c = char.ToUpperInvariant(t[0]);
        return c is >= 'A' and <= 'Z' ? c : '#';
    }

    /// <summary>Case-insensitive match on the term, its aliases and its short definition (what the search box promises).</summary>
    public static bool Matches(GlossaryTerm t, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return t.Term.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))
            || t.ShortDef.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Plain-text block copied by the "Copy definition" button.</summary>
    public static string CopyText(GlossaryTerm t)
    {
        var sb = new StringBuilder();
        sb.Append(t.Term).Append(": ").Append(t.ShortDef.Trim());
        if (!string.IsNullOrWhiteSpace(t.Details)) sb.Append("\n\n").Append(t.Details.Trim());
        if (!string.IsNullOrWhiteSpace(t.WhenToUse)) sb.Append("\n\nWhen to use: ").Append(t.WhenToUse.Trim());
        return sb.ToString();
    }
}

/// <summary>Geometry for the SVG progress rings on the Learning Hub.</summary>
public static class ProgressRing
{
    /// <summary>Whole-number percent, clamped to 0-100. Zero total counts as 0%, not a divide-by-zero.</summary>
    public static int Percent(int done, int total) =>
        total <= 0 ? 0 : (int)Math.Round(Math.Clamp(done, 0, total) * 100.0 / total, MidpointRounding.AwayFromZero);

    public static double Circumference(double radius) => 2 * Math.PI * radius;

    /// <summary>stroke-dashoffset that leaves <paramref name="percent"/> of the ring drawn.</summary>
    public static double DashOffset(double radius, double percent) =>
        Circumference(radius) * (1 - Math.Clamp(percent, 0, 100) / 100.0);

    /// <summary>SVG attribute text with an invariant decimal point (a comma would break the attribute in some locales).</summary>
    public static string Svg(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public enum UptimeDay { NoData, Up, Down }

/// <summary>
/// Daily up/down strip ("sparkline") for a source's last N days. SourceHealthService only exposes summaries, so the page-level
/// strip reads the same App_Data/source-health.json read-only, with a short cache (see <see cref="ReadAll"/>).
/// </summary>
public static class UptimeHistory
{
    /// <summary>One entry per calendar day, oldest first, ending today. Days without a record are <see cref="UptimeDay.NoData"/>.</summary>
    public static IReadOnlyList<UptimeDay> Strip(IReadOnlyDictionary<string, bool>? days, DateOnly today, int span = 30)
    {
        var result = new UptimeDay[Math.Max(1, span)];
        for (var i = 0; i < result.Length; i++)
        {
            var d = today.AddDays(i - (result.Length - 1));
            result[i] = days is not null && days.TryGetValue(d.ToString("yyyy-MM-dd"), out var ok)
                ? (ok ? UptimeDay.Up : UptimeDay.Down)
                : UptimeDay.NoData;
        }
        return result;
    }

    /// <summary>Healthy share of recorded days, or null when nothing has been recorded yet.</summary>
    public static int? UptimePercent(IReadOnlyList<UptimeDay> strip)
    {
        var recorded = strip.Count(d => d != UptimeDay.NoData);
        return recorded == 0 ? null : (int)Math.Round(strip.Count(d => d == UptimeDay.Up) * 100.0 / recorded);
    }

    /// <summary>"ok" (90%+), "warn" (60-89%), "bad" (&lt;60%) or "none" - the status chip tone used across the admin tables.</summary>
    public static string Tone(double? uptimePercent) => uptimePercent switch
    {
        null => "none",
        >= 90 => "ok",
        >= 60 => "warn",
        _ => "bad"
    };

    private static readonly object CacheLock = new();
    private static (string Path, DateTime ReadAt, Dictionary<string, Dictionary<string, bool>> Data)? _cache;

    /// <summary>
    /// Per-source day map from App_Data/source-health.json, cached for 20 seconds. Read-only and fail-soft: a missing,
    /// half-written or old-shape file just yields an empty map, so the strip shows "no data" rather than breaking the page.
    /// </summary>
    public static IReadOnlyDictionary<string, Dictionary<string, bool>> ReadAll(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "App_Data", "source-health.json");
        lock (CacheLock)
        {
            if (_cache is { } c && c.Path == path && DateTime.UtcNow - c.ReadAt < TimeSpan.FromSeconds(20))
                return c.Data;
        }

        var data = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                foreach (var source in doc.RootElement.EnumerateObject())
                {
                    if (source.Value.ValueKind != JsonValueKind.Object || !source.Value.TryGetProperty("Days", out var days) || days.ValueKind != JsonValueKind.Object)
                        continue;
                    var map = new Dictionary<string, bool>();
                    foreach (var day in days.EnumerateObject())
                        if (day.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            map[day.Name] = day.Value.GetBoolean();
                    data[source.Name] = map;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Mid-write or unreadable: show "no data" this time, the next read (20s later) picks it up.
        }

        lock (CacheLock) _cache = (path, DateTime.UtcNow, data);
        return data;
    }
}

/// <summary>Sortable columns of the Tools &amp; Tips comparison matrix.</summary>
public static class ToolMatrix
{
    public static IEnumerable<ToolEntry> Sort(IEnumerable<ToolEntry> tools, string column, bool ascending, IReadOnlyDictionary<string, int> stars)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<ToolEntry> ordered = column switch
        {
            // Tools with no known star count always sink to the bottom, whichever way the column is sorted.
            "stars" => ascending
                ? tools.OrderBy(t => stars.ContainsKey(t.Name) ? 0 : 1).ThenBy(t => stars.GetValueOrDefault(t.Name))
                : tools.OrderBy(t => stars.ContainsKey(t.Name) ? 0 : 1).ThenByDescending(t => stars.GetValueOrDefault(t.Name)),
            "name" => ascending ? tools.OrderBy(t => t.Name, cmp) : tools.OrderByDescending(t => t.Name, cmp),
            _ => ascending ? tools.OrderBy(t => t.Category, cmp) : tools.OrderByDescending(t => t.Category, cmp),
        };
        return ordered.ThenBy(t => t.Name, cmp);
    }

    public static string FormatStars(int n) =>
        n >= 1000 ? (n / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k" : n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record SettingsSection(string Id, string Label, string Icon, bool AdminOnly);

/// <summary>The Settings page's section navigation (sticky rail on desktop, a select on mobile).</summary>
public static class SettingsNav
{
    public static readonly IReadOnlyList<SettingsSection> Sections = new[]
    {
        new SettingsSection("profile", "Profile", "user", false),
        new SettingsSection("feed", "Feed & filters", "rss", false),
        new SettingsSection("appearance", "Appearance", "sparkle", false),
        new SettingsSection("notifications", "Notifications", "bell", false),
        new SettingsSection("integrations", "Integrations", "webhook", false),
        new SettingsSection("security", "Security", "key", false),
        new SettingsSection("admin", "Admin", "database", true),
    };

    public static IEnumerable<SettingsSection> For(bool isAdmin) => Sections.Where(s => isAdmin || !s.AdminOnly);

    /// <summary>A requested section id if it exists (and is visible to this user), otherwise the first section.</summary>
    public static string Resolve(string? requested, bool isAdmin) =>
        For(isAdmin).FirstOrDefault(s => string.Equals(s.Id, requested, StringComparison.OrdinalIgnoreCase))?.Id ?? Sections[0].Id;
}

/// <summary>Splits an Ask answer into plain text and [n] citation markers.</summary>
public static class AskCitations
{
    private static readonly Regex CitePattern = new(@"\[(\d+)\]", RegexOptions.Compiled);

    public static IEnumerable<(string? Text, int? Cite)> Segments(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) yield break;
        var last = 0;
        foreach (Match m in CitePattern.Matches(answer))
        {
            if (m.Index > last) yield return (answer[last..m.Index], null);
            if (int.TryParse(m.Groups[1].Value, out var n)) yield return (null, n);
            last = m.Index + m.Length;
        }
        if (last < answer.Length) yield return (answer[last..], null);
    }
}

/// <summary>Small helpers for the Sources/Users admin tables.</summary>
public static class AdminTable
{
    /// <summary>A detached copy of a source with only Enabled changed - what the bulk Enable/Disable actions save.</summary>
    public static SourceRecord WithEnabled(SourceRecord s, bool enabled) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Url = s.Url,
        Category = s.Category,
        Enabled = enabled,
        Note = s.Note,
        ContentType = s.ContentType,
        Level = s.Level,
        TagsCsv = s.TagsCsv,
        FullTextFetch = s.FullTextFetch,
        IsScrape = s.IsScrape,
        ScrapeItemXPath = s.ScrapeItemXPath,
        ScrapeTitleXPath = s.ScrapeTitleXPath,
        ScrapeLinkXPath = s.ScrapeLinkXPath,
        ScrapeDateXPath = s.ScrapeDateXPath
    };

    public static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

    /// <summary>"3 min ago" / "5 h ago" / "2 d ago" / a date - compact relative time for table cells.</summary>
    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w) return "never";
        var d = now - w;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return w.ToLocalTime().ToString("MMM d, yyyy");
    }
}
