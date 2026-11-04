using System.Collections.Concurrent;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>
/// Pulls and normalizes items from all enabled RSS/Atom feeds. Pure HTTP + XML parsing - no AI involved.
/// Results are cached briefly so navigating between pages doesn't re-hit the network every time.
///
/// Orchestration only - the actual parsing/dedup/scraping/text-cleanup logic that used to live here
/// (all pure functions with no dependency on this class's instance state) now lives in
/// FeedXmlParser, FeedDeduplicator, FeedScraper, FeedTextUtils and FeedDomainThrottle.
/// </summary>
public sealed class FeedAggregatorService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpFactory;
    private readonly KnowledgeBaseService _kb;
    private readonly SourceHealthService _health;
    private readonly ContentExtractorService _extractor;
    private readonly WebSubService _webSub;
    private readonly ILogger<FeedAggregatorService> _log;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private FeedResult? _cache;

    /// <summary>Polls so far; drives which slice of a rate-limit-prone host's sources is due (see <see cref="FeedPollOrder.SelectForRound"/>).</summary>
    private int _round;

    /// <summary>Each source's items from its last successful fetch, so a source skipped this round keeps its place in the cached result instead of vanishing from it until its turn.</summary>
    private readonly ConcurrentDictionary<string, List<FeedItem>> _lastItemsBySource = new();

    public FeedAggregatorService(IHttpClientFactory httpFactory, KnowledgeBaseService kb, SourceHealthService health, ContentExtractorService extractor, WebSubService webSub, ILogger<FeedAggregatorService> log)
    {
        _httpFactory = httpFactory;
        _kb = kb;
        _health = health;
        _extractor = extractor;
        _webSub = webSub;
        _log = log;
    }

    private Dictionary<string, string>? _tagVocab; // matched word/phrase (lowercase) -> canonical tag
    private readonly ConcurrentDictionary<string, int> _failureStreaks = new();

    public DateTimeOffset? LastFetched => _cache?.FetchedAt;

    /// <summary>
    /// Raised as each individual source finishes during a fetch, with a running (not yet deduped) snapshot
    /// of every item fetched so far - lets a page render progressively (source by source) instead of
    /// waiting for the entire batch via <see cref="GetAsync"/>, which with 100+ sources sharing a
    /// rate-limited host can take many minutes. Snapshot items may briefly include near-duplicates across
    /// sources; the final result from GetAsync() is still the properly-deduped one.
    /// </summary>
    public event Action<IReadOnlyList<FeedItem>>? PartialProgress;

    /// <summary>
    /// Raised whenever the cached result is replaced: after every full poll (including the background watcher's, which runs whether or
    /// not anyone has a page open) and after a WebSub push is merged in. Lets open pages show new items without a reload. Handlers run on
    /// the polling thread, so a UI subscriber must marshal with InvokeAsync, and must unsubscribe when it is disposed.
    /// </summary>
    public event Action<FeedResult>? CacheUpdated;

    private void RaiseCacheUpdated(FeedResult result)
    {
        // One listener at a time: a page that throws must not stop the poll, nor silence the pages after it in the invocation list.
        foreach (var listener in CacheUpdated?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<FeedResult>)listener)(result); }
            catch (Exception ex) { _log.LogDebug(ex, "CacheUpdated subscriber threw"); }
        }
    }

    /// <summary>Consecutive failed fetches for a source since its last success (0 = healthy or unknown).</summary>
    public int GetFailureStreak(string sourceName) => _failureStreaks.GetValueOrDefault(sourceName);

    /// <summary>
    /// The current cache, or null if nothing has been fetched yet this run - never triggers a fetch, unlike
    /// GetAsync(). For callers that render on every page (e.g. the sidebar's unread-count badge) and must
    /// never be the thing that kicks off a live poll across 100+ sources sharing a rate-limited host, which
    /// can take minutes and would otherwise block that unrelated page's render until it finished.
    /// </summary>
    public FeedResult? PeekCache() => _cache;

    /// <summary>Returns cached results if fresh; otherwise fetches. Pass force=true to bypass the cache.</summary>
    public async Task<FeedResult> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is not null && DateTimeOffset.Now - _cache.FetchedAt < CacheFor)
            return _cache;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have just refreshed while we waited.
            if (!force && _cache is not null && DateTimeOffset.Now - _cache.FetchedAt < CacheFor)
                return _cache;

            _cache = await FetchAllAsync(ct);
            RaiseCacheUpdated(_cache);
            return _cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<FeedResult> FetchAllAsync(CancellationToken ct)
    {
        var enabled = _kb.Sources.Where(s => s.Enabled).ToList();
        var (due, notDue) = FeedPollOrder.SelectForRound(enabled, Interlocked.Increment(ref _round) - 1);
        var sources = FeedPollOrder.Order(due);
        if (notDue.Count > 0)
            _log.LogInformation("Poll {Round}: fetching {Due} of {Total} sources; {Skipped} rate-limit-prone sources wait for a later poll", _round, due.Count, enabled.Count, notDue.Count);
        var items = new ConcurrentBag<FeedItem>();
        var errors = new ConcurrentBag<string>();

        // Limit concurrency so we're polite to feed hosts.
        using var gate = new SemaphoreSlim(5);

        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(ct);
            var sw = new System.Diagnostics.Stopwatch();
            try
            {
                sw.Start(); // only measures the actual fetch - FetchOneAsync awaits its own per-host cooldown slot first
                var fetched = await FetchOneAsync(source, ct);
                // Only rotated hosts need the carry-over; keeping every source's items would just duplicate the cache.
                if (FeedPollOrder.BatchSizeFor(FeedDomainThrottle.GetHost(source.Url)) is not null) _lastItemsBySource[source.Name] = fetched;
                foreach (var item in fetched)
                    items.Add(item);
                _failureStreaks[source.Name] = 0;
                _health.RecordResult(source.Name, true, sw.Elapsed);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Feed failed: {Source}", source.Name);
                errors.Add($"{source.Name}: {ex.Message}");
                _failureStreaks.AddOrUpdate(source.Name, 1, (_, n) => n + 1);
                _health.RecordResult(source.Name, false, sw.IsRunning ? sw.Elapsed : null, ex.Message);
            }
            finally
            {
                gate.Release();
                // Snapshot after every source (success or failure) so subscribers see steady, real progress
                // rather than long silent gaps while a rate-limited source retries.
                try { PartialProgress?.Invoke(items.OrderByDescending(i => i.Published).ToList()); }
                catch (Exception ex) { _log.LogDebug(ex, "PartialProgress subscriber threw"); }
            }
        });

        await Task.WhenAll(tasks);

        // Sources that were not due this round are not "gone": carry what they last returned into the result.
        foreach (var source in notDue)
            if (_lastItemsBySource.TryGetValue(source.Name, out var previous))
                foreach (var item in previous) items.Add(item);

        return new FeedResult
        {
            Items = FeedDeduplicator.Deduplicate(items).OrderByDescending(i => i.Published).ToList(),
            Errors = errors.OrderBy(e => e).ToList(),
            FetchedAt = DateTimeOffset.Now
        };
    }

    /// <summary>How many of a source's newest items get a full-text fetch each poll - bounded so a big feed doesn't hammer its own site.</summary>
    private const int FullTextFetchLimit = 5;

    private async Task<List<FeedItem>> FetchOneAsync(FeedSource source, CancellationToken ct)
    {
        List<FeedItem> items;
        string? hubUrl = null;

        if (source.IsScrape)
        {
            await FeedDomainThrottle.WaitForSlotAsync(source.Url, ct);
            items = await FeedScraper.ScrapeAsync(source, ct);
        }
        else
        {
            var client = _httpFactory.CreateClient("feeds");
            var host = FeedDomainThrottle.GetHost(source.Url);

            // Retry on 429 (Too Many Requests) with exponential backoff.
            HttpResponseMessage? resp = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await FeedDomainThrottle.WaitForSlotAsync(source.Url, ct);
                resp?.Dispose();
                resp = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (host is not null) FeedDomainThrottle.LearnCooldownFromResponse(host, resp);
                if ((int)resp.StatusCode != 429) break;

                if (attempt < 2) // don't wait on last attempt
                {
                    // Prefer the host's own stated reset time (standard Retry-After, or Reddit's
                    // "x-ratelimit-reset") over a blind guess - verified live that Reddit sends the latter
                    // with a genuinely useful value even though it never sends Retry-After.
                    var retryAfter = resp.Headers.RetryAfter?.Delta
                        ?? FeedDomainThrottle.GetLearnedCooldown(host)
                        ?? TimeSpan.FromSeconds(5 * (attempt + 1));
                    _log.LogWarning("Rate-limited (429) on {Source}, waiting {Wait}s (attempt {Attempt})", source.Name, retryAfter.TotalSeconds, attempt + 1);
                    await Task.Delay(retryAfter, ct);
                }
            }

            resp!.EnsureSuccessStatusCode();
            var xml = await resp.Content.ReadAsStringAsync(ct);

            // Primary: strict RSS/Atom parsing. Fallback: lenient XDocument parse for feeds
            // that don't fit the strict schema (some blogs emit mixed content the strict reader rejects).
            try
            {
                (items, hubUrl) = FeedXmlParser.ParseWithSyndication(xml, source);
            }
            catch (Exception)
            {
                (items, hubUrl) = FeedXmlParser.ParseLenient(xml, source);
            }

            if (hubUrl is not null)
                await MaybeSubscribeAsync(source, hubUrl, ct);
        }

        var vocab = GetTagVocab();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i] with { Tags = TagFor(items[i], vocab) };

        if (source.FullTextFetch && items.Count > 0)
            items = await ApplyFullTextAsync(items, ct);

        return items;
    }

    /// <summary>
    /// If the feed declares a WebSub hub and we're not already subscribed, ask the hub to push future
    /// updates to us instead of waiting for the next poll. No-op unless WebSub:PublicBaseUrl is configured.
    /// </summary>
    private async Task MaybeSubscribeAsync(FeedSource source, string hubUrl, CancellationToken ct)
    {
        if (!_webSub.Enabled || source.Id == 0) return;
        try
        {
            if (!await _webSub.HasActiveSubscriptionAsync(source.Id))
                await _webSub.SubscribeAsync(source.Id, source.Url, hubUrl, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebSub subscribe check failed for {Source}", source.Name);
        }
    }

    /// <summary>
    /// Merges items pushed by a WebSub hub into the live cache immediately, instead of waiting for the
    /// next scheduled poll. Called from the /websub/callback POST endpoint.
    /// </summary>
    public async Task<List<FeedItem>> MergePushedContentAsync(FeedSource source, string xml, CancellationToken ct = default)
    {
        List<FeedItem> items;
        try
        {
            (items, _) = FeedXmlParser.ParseWithSyndication(xml, source);
        }
        catch (Exception)
        {
            (items, _) = FeedXmlParser.ParseLenient(xml, source);
        }

        var vocab = GetTagVocab();
        for (var i = 0; i < items.Count; i++)
            items[i] = items[i] with { Tags = TagFor(items[i], vocab) };

        if (items.Count == 0)
            return items;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_cache is null)
            {
                // Nothing polled yet this run - a push arriving first is fine, just seed the cache with it.
                _cache = new FeedResult { Items = items, FetchedAt = DateTimeOffset.Now };
            }
            else
            {
                var existingLinks = new HashSet<string>(_cache.Items.Select(i => i.Link), StringComparer.OrdinalIgnoreCase);
                var merged = _cache.Items.Concat(items.Where(i => !existingLinks.Contains(i.Link)));
                _cache = new FeedResult
                {
                    Items = FeedDeduplicator.Deduplicate(merged).OrderByDescending(i => i.Published).ToList(),
                    Errors = _cache.Errors,
                    FetchedAt = _cache.FetchedAt
                };
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        if (_cache is not null) RaiseCacheUpdated(_cache);
        return items;
    }

    /// <summary>Fetches the full article for the newest few items and attaches it as FullText, so summary-only feeds become readable in-app.</summary>
    private async Task<List<FeedItem>> ApplyFullTextAsync(List<FeedItem> items, CancellationToken ct)
    {
        // Items whose own feed content was already substantial got FullText set at parse time (see
        // FeedXmlParser.FeedContentSubstantialThreshold) - no need to hit the network for those, and
        // skipping them means more of the FullTextFetchLimit budget goes to items that actually need a
        // live fetch.
        var targets = items.Where(i => i.FullText is null).OrderByDescending(i => i.Published).Take(FullTextFetchLimit).ToList();
        var fullTexts = new Dictionary<string, string?>();

        using var gate = new SemaphoreSlim(3);
        var tasks = targets.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                fullTexts[item.Link] = await _extractor.FetchFullTextAsync(item.Link, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        for (var i = 0; i < items.Count; i++)
        {
            if (fullTexts.TryGetValue(items[i].Link, out var text) && text is not null)
                items[i] = items[i] with { FullText = text };
        }
        return items;
    }

    /// <summary>Builds a lowercase term/alias -> canonical tag lookup from the curated glossary, once.</summary>
    private Dictionary<string, string> GetTagVocab()
    {
        if (_tagVocab is not null)
            return _tagVocab;

        var vocab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in _kb.Glossary)
        {
            vocab.TryAdd(term.Term, term.Term);
            foreach (var alias in term.Aliases)
                vocab.TryAdd(alias, term.Term);
        }
        return _tagVocab = vocab;
    }

    /// <summary>Union of the source's base tags plus any glossary terms/aliases matched in the item's title+summary.</summary>
    private static string[] TagFor(FeedItem item, Dictionary<string, string> vocab)
    {
        var text = item.Title + " " + item.Summary;
        var tags = new HashSet<string>(item.Tags, StringComparer.OrdinalIgnoreCase);

        foreach (var (needle, canonical) in vocab)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(needle)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                tags.Add(canonical);
        }

        return tags.ToArray();
    }

    /// <summary>Feeds without a per-item title used to show as a bare "(untitled)" - kept as a thin
    /// forwarder since <see cref="ReadingStateService"/>'s bookmark-repair helper already calls this
    /// exact static path.</summary>
    public static string DeriveTitle(string? rawTitle, string cleanedSummary, string sourceName) =>
        FeedTextUtils.DeriveTitle(rawTitle, cleanedSummary, sourceName);
}
