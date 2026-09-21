using System.Text.RegularExpressions;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Merges items that are almost certainly the same story reported by multiple sources - first by an
/// exact normalized-title key (significant words, sorted), then by fuzzy token-overlap between the
/// *remaining* distinct keys (catches "OpenAI releases GPT-5" vs "OpenAI's GPT-5 launches" - different
/// wording, same story - which don't share an exact key but do share most of their significant words),
/// both within a 3-day window so a big release covered by several blogs shows up once. Pure string
/// matching, no AI involved.
/// </summary>
internal static class FeedDeduplicator
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "your", "you", "are", "was", "were",
        "into", "how", "why", "what", "new", "now", "will", "can", "has", "have", "its", "our"
    };

    // Two titles need to share at least this fraction of their significant words - as an overlap
    // coefficient (intersection / size of the SMALLER set), not Jaccard (intersection / union) - to be
    // treated as the same story despite not matching exactly. Overlap coefficient rather than Jaccard
    // because real near-duplicate headlines vary a lot in length ("X releases Y" vs "X's Y launches with
    // Z capabilities"), which Jaccard punishes hard even when every word in the shorter title is also in
    // the longer one; traced concrete examples landed on 0.5 as the line between "same story, reworded"
    // (~0.5-0.67 overlap in practice) and "different stories sharing a couple of generic terms" (~0.3).
    private const double FuzzyDuplicateThreshold = 0.5;

    public static IEnumerable<FeedItem> Deduplicate(IEnumerable<FeedItem> items)
    {
        var groups = new Dictionary<string, List<FeedItem>>();
        var keyOrder = new List<string>();
        foreach (var item in items)
        {
            var key = NormalizedTitleKey(item.Title);
            if (key is null)
            {
                yield return item; // title too short/generic to safely dedupe - keep as-is
                continue;
            }
            if (!groups.TryGetValue(key, out var bucket))
            {
                groups[key] = bucket = new List<FeedItem>();
                keyOrder.Add(key);
            }
            bucket.Add(item);
        }

        foreach (var bucket in MergeFuzzyDuplicates(keyOrder, groups))
        {
            if (bucket.Count == 1)
            {
                yield return bucket[0];
                continue;
            }

            // Split into sub-clusters by publish time (within 3 days of each other) - avoids merging
            // an old and a new story that happen to share generic significant words.
            foreach (var cluster in ClusterByTime(bucket))
            {
                if (cluster.Count == 1)
                {
                    yield return cluster[0];
                    continue;
                }

                var primary = cluster.OrderBy(i => i.Published).First();
                var others = cluster.Where(i => i != primary).Select(i => i.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var mergedTags = cluster.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                yield return primary with { AlsoSeenOn = others, Tags = mergedTags };
            }
        }
    }

    /// <summary>
    /// Second dedup pass over the exact-key groups: merges any two whose titles are still near-duplicates
    /// (high word-set overlap) and whose earliest items are within 3 days of each other - same rationale
    /// as the exact-key window, applied here so two differently-worded reports of the same release still
    /// merge instead of just reducing exact repeats.
    /// </summary>
    private static List<List<FeedItem>> MergeFuzzyDuplicates(List<string> keyOrder, Dictionary<string, List<FeedItem>> groups)
    {
        var wordSets = keyOrder.ToDictionary(k => k, k => new HashSet<string>(k.Split(' ')));
        var consumed = new HashSet<string>();
        var result = new List<List<FeedItem>>();

        foreach (var key in keyOrder)
        {
            if (!consumed.Add(key)) continue; // already folded into an earlier bucket
            var combined = new List<FeedItem>(groups[key]);
            var earliest = groups[key].Min(i => i.Published);

            foreach (var otherKey in keyOrder)
            {
                if (otherKey == key || consumed.Contains(otherKey)) continue;

                var otherEarliest = groups[otherKey].Min(i => i.Published);
                if (Math.Abs((otherEarliest - earliest).TotalDays) > 3) continue;

                if (OverlapCoefficient(wordSets[key], wordSets[otherKey]) >= FuzzyDuplicateThreshold)
                {
                    combined.AddRange(groups[otherKey]);
                    consumed.Add(otherKey);
                }
            }

            result.Add(combined);
        }
        return result;
    }

    private static double OverlapCoefficient(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / Math.Min(a.Count, b.Count);
    }

    private static List<List<FeedItem>> ClusterByTime(List<FeedItem> items)
    {
        var sorted = items.OrderBy(i => i.Published).ToList();
        var clusters = new List<List<FeedItem>>();
        foreach (var item in sorted)
        {
            var cluster = clusters.LastOrDefault();
            if (cluster is not null && item.Published - cluster[^1].Published <= TimeSpan.FromDays(3))
                cluster.Add(item);
            else
                clusters.Add(new List<FeedItem> { item });
        }
        return clusters;
    }

    /// <summary>Lowercase, strip punctuation, drop stopwords/short words, sort what's left. Null if too little signal.</summary>
    private static string? NormalizedTitleKey(string title)
    {
        var words = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\s]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !Stopwords.Contains(w))
            .Distinct()
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        return words.Count < 3 ? null : string.Join(' ', words);
    }
}
