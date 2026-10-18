namespace AiPulse.Services.Ai.Rag;

public sealed record CorpusStats(int Documents, IReadOnlyDictionary<string, int> ByKind, DateTimeOffset BuiltAt);

/// <summary>
/// The searchable body of knowledge. The first search builds it; after that a search past the refresh interval
/// keeps answering from the existing index while a background rebuild picks up new feed items and edited
/// knowledge-base content, so nobody waits on a rebuild and no restart is needed.
/// </summary>
public sealed class KnowledgeCorpus
{
    private sealed record Snapshot(Bm25Index Index, CorpusStats Stats);

    private readonly Func<IReadOnlyList<KnowledgeDocument>> _load;
    private readonly TimeSpan _refreshEvery;
    private readonly Action<Exception>? _onRefreshError;
    private readonly object _buildLock = new();
    private volatile Snapshot? _snapshot;
    private int _refreshing;

    public KnowledgeCorpus(Func<IReadOnlyList<KnowledgeDocument>> load, TimeSpan refreshEvery, Action<Exception>? onRefreshError = null)
    {
        _load = load;
        _refreshEvery = refreshEvery;
        _onRefreshError = onRefreshError;
    }

    public CorpusStats Stats => Current().Stats;

    /// <summary>Passages relevant to <paramref name="query"/>, best first, dropping any that match too little of it.</summary>
    public IReadOnlyList<ScoredDocument> Search(string query, int topK, double minCoverage) =>
        Current().Index.Search(query, topK * 3)
            .Where(s => s.Coverage >= minCoverage)
            .Take(topK)
            .ToList();

    private Snapshot Current()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            lock (_buildLock)
                return _snapshot ??= Build();
        }

        var stale = DateTimeOffset.UtcNow - snapshot.Stats.BuiltAt >= _refreshEvery;
        if (stale && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try { _snapshot = Build(); }
                catch (Exception ex) { _onRefreshError?.Invoke(ex); }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }

        return snapshot;
    }

    private Snapshot Build()
    {
        var docs = _load();
        var stats = new CorpusStats(
            docs.Count,
            docs.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()),
            DateTimeOffset.UtcNow);
        return new Snapshot(Bm25Index.Build(docs), stats);
    }
}
