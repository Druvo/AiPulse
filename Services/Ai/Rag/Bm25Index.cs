namespace AiPulse.Services.Ai.Rag;

/// <summary>In-memory BM25 over <see cref="KnowledgeDocument"/>s. Title and keyword matches count extra; news gets a small recency lift.</summary>
public sealed class Bm25Index
{
    private const double K1 = 1.4;
    private const double B = 0.75;
    private const int TitleWeight = 3;
    private const int KeywordWeight = 2;

    // "What is RAG?" should land on the entry called RAG, not on a long guide that mentions it twenty times.
    // When every word of an entry's title appears in the question, the person has named the entry.
    private const double TitleNamedBoost = 1.8;

    private readonly List<KnowledgeDocument> _docs = new();
    private readonly List<Dictionary<string, int>> _termFrequency = new();
    private readonly List<int> _length = new();
    private readonly List<HashSet<string>> _titleTokens = new();
    private readonly Dictionary<string, List<int>> _postings = new(StringComparer.Ordinal);
    private double _averageLength;

    public int Count => _docs.Count;

    public static Bm25Index Build(IEnumerable<KnowledgeDocument> docs)
    {
        var index = new Bm25Index();
        foreach (var doc in docs)
            index.Add(doc);
        index._averageLength = index._docs.Count == 0 ? 1 : Math.Max(1, index._length.Average());
        return index;
    }

    private void Add(KnowledgeDocument doc)
    {
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;

        void Count(IEnumerable<string> tokens, int weight)
        {
            foreach (var t in tokens)
            {
                tf[t] = tf.GetValueOrDefault(t) + weight;
                length += weight;
            }
        }

        Count(Tokenizer.Tokens(doc.Title), TitleWeight);
        Count(Tokenizer.Tokens(doc.Keywords), KeywordWeight);
        Count(Tokenizer.Tokens(doc.Text), 1);

        var id = _docs.Count;
        _docs.Add(doc);
        _titleTokens.Add(new HashSet<string>(Tokenizer.Tokens(doc.Title), StringComparer.Ordinal));
        _termFrequency.Add(tf);
        _length.Add(Math.Max(1, length));

        foreach (var term in tf.Keys)
        {
            if (!_postings.TryGetValue(term, out var list))
                _postings[term] = list = new List<int>();
            list.Add(id);
        }
    }

    public IReadOnlyList<ScoredDocument> Search(string query, int k, DateTimeOffset? now = null)
    {
        var terms = Tokenizer.Tokens(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || _docs.Count == 0) return Array.Empty<ScoredDocument>();

        var scores = new Dictionary<int, double>();
        var matched = new Dictionary<int, List<string>>();
        var n = _docs.Count;

        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var posting)) continue;

            var idf = Math.Log(1 + (n - posting.Count + 0.5) / (posting.Count + 0.5));
            foreach (var id in posting)
            {
                var tf = _termFrequency[id][term];
                var norm = tf + K1 * (1 - B + B * _length[id] / _averageLength);
                scores[id] = scores.GetValueOrDefault(id) + idf * tf * (K1 + 1) / norm;

                if (!matched.TryGetValue(id, out var list))
                    matched[id] = list = new List<string>();
                list.Add(term);
            }
        }

        var termSet = new HashSet<string>(terms, StringComparer.Ordinal);
        var clock = now ?? DateTimeOffset.UtcNow;
        return scores
            .Select(kv =>
            {
                var doc = _docs[kv.Key];
                var score = kv.Value;
                var title = _titleTokens[kv.Key];
                if (title.Count > 0 && title.IsSubsetOf(termSet)) score *= TitleNamedBoost;
                if (doc.Published is { } published)
                {
                    var ageDays = Math.Max(0, (clock - published).TotalDays);
                    score *= 1 + 0.2 * Math.Exp(-ageDays / 45.0);
                }
                var hits = matched[kv.Key];
                return new ScoredDocument(doc, score, (double)hits.Count / terms.Count, hits);
            })
            .OrderByDescending(s => s.Score)
            .Take(k)
            .ToList();
    }
}
