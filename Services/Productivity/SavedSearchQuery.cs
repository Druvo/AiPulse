using System.Security.Cryptography;
using System.Text;
using AiPulse.Models;

namespace AiPulse.Services.Productivity;

/// <summary>
/// A parsed saved search. Keyword syntax, kept deliberately small:
/// <list type="bullet">
/// <item>plain words must all appear (title, summary or tags; case-insensitive substring)</item>
/// <item>"quoted phrases" must appear verbatim</item>
/// <item>-word or -"phrase" must not appear</item>
/// <item>tag:name and source:"Name" inside the keywords add to the tag/source lists</item>
/// </list>
/// Tags match when the item carries any of them; sources match when the item came from any of them. Everything given must hold.
/// </summary>
public sealed record SavedSearchQuery(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Sources)
{
    public const int MaxKeywordsLength = 300;

    public bool IsEmpty => Terms.Count == 0 && Excluded.Count == 0 && Tags.Count == 0 && Sources.Count == 0;

    /// <summary>A rule with only exclusions would match almost everything, so it does not count as a real query.</summary>
    public bool HasPositiveCriterion => Terms.Count > 0 || Tags.Count > 0 || Sources.Count > 0;

    public static SavedSearchQuery From(SavedSearchRule rule) => Parse(rule.Keywords, rule.Tags, rule.Sources);

    public static SavedSearchQuery Parse(string? keywords, string? tags = null, string? sources = null)
    {
        var terms = new List<string>();
        var excluded = new List<string>();
        var tagList = SplitList(tags, ',').ToList();
        var sourceList = SplitList(sources, '\n').ToList();

        foreach (var (token, negated) in Tokenise(keywords ?? ""))
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
            {
                if (!negated) tagList.Add(token[4..]);
                continue;
            }
            if (token.StartsWith("source:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
            {
                if (!negated) sourceList.Add(token[7..]);
                continue;
            }
            (negated ? excluded : terms).Add(token);
        }

        return new SavedSearchQuery(
            Distinct(terms), Distinct(excluded), Distinct(tagList), Distinct(sourceList));
    }

    public bool Matches(FeedItem item)
    {
        if (IsEmpty) return false;

        if (Sources.Count > 0 && !Sources.Any(s => string.Equals(s, item.SourceName, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (Tags.Count > 0 && !item.Tags.Any(t => Tags.Any(w => string.Equals(w, t.Trim(), StringComparison.OrdinalIgnoreCase))))
            return false;

        if (Terms.Count == 0 && Excluded.Count == 0) return true;

        var haystack = item.Title + "\n" + item.Summary + "\n" + string.Join(' ', item.Tags);
        foreach (var term in Terms)
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var term in Excluded)
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>How many of the given items match, optionally only those published at or after <paramref name="since"/>.</summary>
    public int Count(IEnumerable<FeedItem> items, DateTimeOffset? since = null) =>
        items.Count(i => (since is null || i.Published >= since) && Matches(i));

    /// <summary>A one-line human description, e.g. <c>"agents" -crypto · tags: MCP · 2 sources</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Terms.Count > 0) parts.Add(string.Join(' ', Terms.Select(t => t.Contains(' ') ? $"\"{t}\"" : t)));
        if (Excluded.Count > 0) parts.Add(string.Join(' ', Excluded.Select(t => "-" + (t.Contains(' ') ? $"\"{t}\"" : t))));
        if (Tags.Count > 0) parts.Add("tags: " + string.Join(", ", Tags));
        if (Sources.Count > 0) parts.Add(Sources.Count == 1 ? "source: " + Sources[0] : $"{Sources.Count} sources");
        return parts.Count == 0 ? "(empty)" : string.Join(" · ", parts);
    }

    /// <summary>Splits keywords into tokens, honouring "double quotes" and a leading '-' for exclusion. Unbalanced quotes run to the end.</summary>
    internal static IEnumerable<(string Token, bool Negated)> Tokenise(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;

            var negated = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                negated = true;
                i++;
            }

            string token;
            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                token = text[(i + 1)..end];
                i = Math.Min(text.Length, end + 1);
            }
            else
            {
                var start = i;
                // A prefix like source:"Hugging Face" keeps its quoted value together.
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    if (text[i] == '"')
                    {
                        var end = text.IndexOf('"', i + 1);
                        i = end < 0 ? text.Length : end + 1;
                        continue;
                    }
                    i++;
                }
                token = text[start..i].Replace("\"", "");
            }

            token = token.Trim();
            if (token.Length > 0) yield return (token, negated);
        }
    }

    public static IEnumerable<string> SplitList(string? value, char separator) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0);

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Stable key for a link in the dedupe table (links can be long; the hash keeps the unique index small).</summary>
    public static string LinkHash(string link) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link)))[..40].ToLowerInvariant();
}
