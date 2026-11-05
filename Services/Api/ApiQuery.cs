using System.Globalization;
using AiPulse.Models;
using Microsoft.AspNetCore.Http;

namespace AiPulse.Services.Api;

/// <summary>Collects per-parameter validation messages for a 400 problem response.</summary>
public sealed class ApiQueryErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    public void Add(string parameter, string message)
    {
        if (!_errors.TryGetValue(parameter, out var list)) _errors[parameter] = list = new List<string>();
        list.Add(message);
    }
    public bool Any => _errors.Count > 0;
    public IReadOnlyDictionary<string, string[]> ToDictionary() => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
}

public sealed record Paging(int Page, int PageSize);

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => Total == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record NewsFilter(
    string? Q, string? Tag, IReadOnlyList<string> Sources, string? Category, string? Type, string? Level,
    DateTimeOffset? Since, DateTimeOffset? Until, bool OldestFirst)
{
    public static readonly NewsFilter None = new(null, null, Array.Empty<string>(), null, null, null, null, null, false);
}

/// <summary>
/// Query-string parsing, filtering, searching and paging for /api/v1. Pure over its inputs (items, query, "now") so the
/// behaviour is unit-testable without a server.
/// </summary>
public static class ApiQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 10_000;
    public const int MaxQueryLength = 200;
    public const int MinSearchLength = 2;

    public static Paging ParsePaging(IQueryCollection query, ApiQueryErrors errors, int defaultPageSize = DefaultPageSize)
    {
        var page = ParseInt(query, "page", 1, 1, MaxPage, errors);
        var size = ParseInt(query, "pageSize", defaultPageSize, 1, MaxPageSize, errors);
        return new Paging(page, size);
    }

    public static int ParseInt(IQueryCollection query, string name, int fallback, int min, int max, ApiQueryErrors errors)
    {
        var raw = query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            errors.Add(name, $"Must be a whole number from {min} to {max}.");
            return fallback;
        }
        return value;
    }

    public static string? ParseText(IQueryCollection query, string name, ApiQueryErrors errors, int maxLength = MaxQueryLength)
    {
        var raw = query[name].ToString().Trim();
        if (raw.Length == 0) return null;
        if (raw.Length > maxLength)
        {
            errors.Add(name, $"Keep it to {maxLength} characters or fewer.");
            return null;
        }
        return raw;
    }

    /// <summary>Repeated (?source=a&amp;source=b) and comma-separated (?source=a,b) values, trimmed and de-duplicated.</summary>
    public static IReadOnlyList<string> ParseList(IQueryCollection query, string name) =>
        query[name].SelectMany(v => (v ?? "").Split(',')).Select(v => v.Trim()).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// An ISO 8601 date or date-time ("2026-11-01", "2026-11-01T08:00:00Z"; no offset means UTC) or a relative span back from
    /// now: "90m", "24h", "7d", "2w".
    /// </summary>
    public static DateTimeOffset? ParseInstant(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length >= 2 && char.IsAsciiLetter(raw[^1]) && int.TryParse(raw[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= 100_000)
        {
            TimeSpan? span = char.ToLowerInvariant(raw[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => null
            };
            return span is { } s ? now - s : null;
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;
    }

    public static NewsFilter ParseNewsFilter(IQueryCollection query, DateTimeOffset now, ApiQueryErrors errors)
    {
        DateTimeOffset? Instant(string name)
        {
            var raw = query[name].ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = ParseInstant(raw, now);
            if (value is null) errors.Add(name, "Use an ISO 8601 date or date-time (2026-11-01, 2026-11-01T08:00:00Z) or a relative span like 24h, 7d.");
            return value;
        }

        var sort = query["sort"].ToString().Trim().ToLowerInvariant();
        if (sort.Length > 0 && sort != "newest" && sort != "oldest") errors.Add("sort", "Use \"newest\" (default) or \"oldest\".");

        var since = Instant("since");
        var until = Instant("until");
        if (since is { } s && until is { } u && s > u) errors.Add("since", "Must not be later than \"until\".");

        return new NewsFilter(
            ParseText(query, "q", errors), ParseText(query, "tag", errors, 100), ParseList(query, "source"),
            ParseText(query, "category", errors, 100), ParseText(query, "type", errors, 100), ParseText(query, "level", errors, 100),
            since, until, sort == "oldest");
    }

    public static IEnumerable<FeedItem> Apply(IEnumerable<FeedItem> items, NewsFilter f)
    {
        var q = items;
        if (f.Tag is { } tag) q = q.Where(i => i.Tags.Any(t => string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase)));
        if (f.Sources.Count > 0)
        {
            var set = new HashSet<string>(f.Sources, StringComparer.OrdinalIgnoreCase);
            q = q.Where(i => set.Contains(i.SourceName));
        }
        if (f.Category is { } c) q = q.Where(i => string.Equals(i.Category, c, StringComparison.OrdinalIgnoreCase));
        if (f.Type is { } type) q = q.Where(i => string.Equals(i.ContentType, type, StringComparison.OrdinalIgnoreCase));
        if (f.Level is { } level) q = q.Where(i => string.Equals(i.Level, level, StringComparison.OrdinalIgnoreCase));
        if (f.Since is { } since) q = q.Where(i => i.Published >= since);
        if (f.Until is { } until) q = q.Where(i => i.Published <= until);
        if (f.Q is { } text)
        {
            var terms = Terms(text);
            q = q.Where(i => MatchesAll(terms, i.Title, i.Summary, i.SourceName, string.Join(' ', i.Tags), i.Author));
        }
        // Ties broken by link so paging is deterministic even when many items share a timestamp.
        return f.OldestFirst
            ? q.OrderBy(i => i.Published).ThenBy(i => i.Link, StringComparer.Ordinal)
            : q.OrderByDescending(i => i.Published).ThenBy(i => i.Link, StringComparer.Ordinal);
    }

    public static PageResult<T> Paginate<T>(IEnumerable<T> all, Paging paging)
    {
        var list = all as IReadOnlyList<T> ?? all.ToList();
        var items = list.Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize).ToList();
        return new PageResult<T>(items, paging.Page, paging.PageSize, list.Count);
    }

    public static string[] Terms(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Every term appears (case-insensitive substring) in at least one of the fields.</summary>
    public static bool MatchesAll(string[] terms, params string?[] fields) =>
        terms.All(term => fields.Any(f => f is not null && f.Contains(term, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Higher is better: whole query in the title, then every term in the title, then a match anywhere.</summary>
    public static int Score(string query, string[] terms, string title, params string?[] rest)
    {
        if (!MatchesAll(terms, rest.Prepend(title).ToArray())) return 0;
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (MatchesAll(terms, title)) return 2;
        return 1;
    }

    /// <summary>The cross-section search behind /api/v1/search: headlines, releases, topics, glossary and tools.</summary>
    public static SearchResultsDto Search(string query, int limit, IReadOnlyList<FeedItem> items, IReadOnlyList<GlossaryTerm> glossary,
        IReadOnlyList<ToolEntry> tools, DateTimeOffset now, string origin)
    {
        var terms = Terms(query);

        IReadOnlyList<NewsItemDto> RankItems(IEnumerable<FeedItem> source) => source
            .Select(i => (Item: i, Score: Score(query, terms, i.Title, i.Summary, i.SourceName, string.Join(' ', i.Tags))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.Published).ThenBy(x => x.Item.Link, StringComparer.Ordinal)
            .Take(limit).Select(x => ApiMapper.News(x.Item)).ToList();

        var news = RankItems(items.Where(PublicFeeds.IsHeadline));
        var releases = RankItems(items.Where(i => !PublicFeeds.IsHeadline(i) && i.Published <= now));

        var topics = PublicFeeds.Topics(items, now.AddDays(-30), take: int.MaxValue)
            .Where(t => MatchesAll(terms, t.Tag))
            .OrderByDescending(t => t.Tag.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenByDescending(t => t.Count)
            .Take(limit).Select(t => ApiMapper.Topic(t, origin)).ToList();

        var glossaryHits = glossary
            .Select(g => (Term: g, Score: Math.Max(
                Score(query, terms, g.Term, g.ShortDef, g.Details, string.Join(' ', g.Aliases)),
                g.Aliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)) ? 4 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Term.Term, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(x => ApiMapper.Glossary(x.Term)).ToList();

        var toolHits = tools
            .Select(t => (Tool: t, Score: Score(query, terms, t.Name, t.OneLiner, t.Category, string.Join(' ', t.BestFor))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(x => ApiMapper.Tool(x.Tool)).ToList();

        return new SearchResultsDto(query, news, releases, topics, glossaryHits, toolHits);
    }
}
