using System.Text.RegularExpressions;

namespace AiPulse.Services;

/// <summary>Pure text/URL cleanup shared by the syndication parser and the HTML scraper: stripping tags
/// from feed content, deriving a readable title when a feed item has none, and stripping tracking
/// params from links before they're stored/displayed/exported.</summary>
internal static class FeedTextUtils
{
    private static readonly Regex HtmlTags = new("<[^>]+>", RegexOptions.Compiled);

    public static string CleanText(string raw, int max)
    {
        var text = HtmlTags.Replace(raw, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > max ? text[..max].TrimEnd() + "…" : text;
    }

    /// <summary>
    /// Feeds without a per-item title (common for microblog-style posts - Mastodon, some YouTube
    /// Community posts, etc.) used to show as a bare "(untitled)", which read as broken rather than
    /// "this platform just doesn't have titles". Derive something readable instead: the raw title if
    /// there is one, otherwise the start of the summary, otherwise a plain fallback naming the source.
    /// </summary>
    public static string DeriveTitle(string? rawTitle, string cleanedSummary, string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            var cleaned = CleanText(rawTitle, 200);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        if (!string.IsNullOrWhiteSpace(cleanedSummary))
        {
            const int max = 80;
            return cleanedSummary.Length > max ? cleanedSummary[..max].TrimEnd() + "…" : cleanedSummary;
        }

        return $"New post from {sourceName}";
    }

    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name", "utm_reader",
        "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid",
        "ref", "ref_src", "ref_url", "_hsenc", "_hsmi", "spm", "yclid", "vero_id", "oly_enc_id", "oly_anon_id"
    };

    /// <summary>Strips known tracking query params from an item's link before it's stored/displayed/exported - privacy hygiene, not functional.</summary>
    public static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return url;

        var baseUrl = url[..qIndex];
        var query = url[(qIndex + 1)..];
        var fragment = "";
        var hashIndex = query.IndexOf('#');
        if (hashIndex >= 0)
        {
            fragment = query[hashIndex..];
            query = query[..hashIndex];
        }

        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !TrackingParams.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0])))
            .ToList();

        return kept.Count == 0 ? baseUrl + fragment : $"{baseUrl}?{string.Join('&', kept)}{fragment}";
    }
}
