using System.Text.RegularExpressions;

namespace AiPulse.Services;

/// <summary>
/// Periodically checks a community-curated "free LLM API" list for providers not yet on AiPulse's own
/// Data/free-apis.json, and re-checks existing entries' signup/docs links for rot. Never publishes
/// anything automatically - new finds land in a review queue (KnowledgeBaseService.AddCandidateIfNewAsync)
/// that an admin approves or dismisses from the Free AI APIs page, which is what keeps the page's
/// "hand-verified" LastVerified dates meaningful.
///
/// There's no structured feed for "which providers currently have a free tier" - the same reason this page
/// started out hand-curated in the first place. This watches the closest real substitute instead of trying
/// to crawl the open web: a GitHub repo (cheahjs/free-llm-api-resources) whose entire purpose is tracking
/// exactly this, kept current by its own maintainer via PRs. Its README is a flat sequence of
/// "### [Provider Name](url)" headings - confirmed by fetching the raw file directly rather than guessing
/// at the format, since a wrong assumption here would silently produce zero candidates forever.
/// </summary>
public sealed class FreeApiDiscoveryService : BackgroundService
{
    private const string SourceListUrl = "https://raw.githubusercontent.com/cheahjs/free-llm-api-resources/main/README.md";
    private const string SourceRepoUrl = "https://github.com/cheahjs/free-llm-api-resources";
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LinkCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex ProviderHeadingRegex = new(@"^### \[([^\]]+)\]\(([^)]+)\)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AnyHeadingRegex = new(@"^#{2,3} ", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ModelsLabelRegex = new(@"\*\*Models:\*\*[ \t]*(.*)", RegexOptions.Compiled);
    private static readonly Regex SummaryLabelRegex = new(@"\*\*(?:Credits|Limits[^*:]*):\*\*[ \t]*(.*)", RegexOptions.Compiled);
    private static readonly Regex MdLinkRegex = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex TableRowRegex = new(@"<tr>\s*<td>([^<]*)</td>", RegexOptions.Compiled);
    private static readonly Regex BulletLineRegex = new(@"^-\s+(.*)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly KnowledgeBaseService _kb;
    private readonly ILogger<FreeApiDiscoveryService> _log;

    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastRunSummary { get; private set; }
    public bool IsRunning { get; private set; }

    public FreeApiDiscoveryService(IHttpClientFactory httpFactory, KnowledgeBaseService kb, ILogger<FreeApiDiscoveryService> log)
    {
        _httpFactory = httpFactory;
        _kb = kb;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        using var timer = new PeriodicTimer(RunInterval);
        do
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Free API discovery run failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs both passes now, outside the daily schedule - backs the admin's "Run now" button on the Free AI APIs page.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        try
        {
            var added = await DiscoverNewProvidersAsync(ct);
            var flagged = await CheckStalenessAsync(ct);
            LastRunAt = DateTimeOffset.Now;
            LastRunSummary = $"{added} new candidate(s) found, {flagged} entr{(flagged == 1 ? "y" : "ies")} flagged for review";
            _log.LogInformation("Free API discovery run complete: {Summary}", LastRunSummary);
            await _kb.LogDiscoveryRunAsync(added, flagged, LastRunSummary);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<int> DiscoverNewProvidersAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        string markdown;
        try
        {
            markdown = await client.GetStringAsync(SourceListUrl, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not fetch free-API discovery source list");
            return 0;
        }

        var added = 0;
        foreach (var p in ParseProviders(markdown))
        {
            if (await _kb.AddCandidateIfNewAsync(p.Name, p.Url, $"Listed at {SourceRepoUrl}", p.Summary, p.Models))
                added++;
        }
        return added;
    }

    internal readonly record struct ProviderInfo(string Name, string Url, string? Summary, string? Models);

    /// <summary>
    /// Beyond the "### [Name](url)" heading, each provider's own section already carries a free-tier
    /// summary (a "**Credits:**" or "**Limits...:**" line) and a model list (a "**Models:**" label, an
    /// HTML table, or a bare bullet list) - both parsed straight from the source's own text, never
    /// invented, so the admin's review form starts with real content instead of a blank field.
    /// </summary>
    internal static List<ProviderInfo> ParseProviders(string markdown)
    {
        var results = new List<ProviderInfo>();
        var headingStarts = AnyHeadingRegex.Matches(markdown).Select(m => m.Index).OrderBy(i => i).ToList();

        foreach (Match m in ProviderHeadingRegex.Matches(markdown))
        {
            var name = m.Groups[1].Value.Trim();
            var url = m.Groups[2].Value.Trim();
            if (name.Length < 2 || results.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var bodyStart = m.Index + m.Length;
            var bodyEnd = headingStarts.FirstOrDefault(i => i > m.Index, markdown.Length);
            var body = bodyStart < bodyEnd ? markdown[bodyStart..bodyEnd] : "";

            results.Add(new ProviderInfo(name, url, ExtractSummary(body), ExtractModels(body)));
        }
        return results;
    }

    private static string? ExtractSummary(string body)
    {
        var m = SummaryLabelRegex.Match(body);
        if (!m.Success) return null;

        var text = m.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            // Label with nothing on the same line - the value is the next non-empty line.
            var rest = body[(m.Index + m.Length)..];
            text = rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        }

        var cleaned = CleanMarkdown(text);
        return string.IsNullOrWhiteSpace(cleaned) ? null : Truncate(cleaned, 220);
    }

    private static string? ExtractModels(string body)
    {
        var labelMatch = ModelsLabelRegex.Match(body);
        if (labelMatch.Success)
        {
            var inline = labelMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inline))
                return Truncate(CleanMarkdown(inline), 300);

            var bulleted = JoinBullets(body[(labelMatch.Index + labelMatch.Length)..]);
            if (bulleted is not null) return Truncate(bulleted, 300);
        }

        // No explicit "Models:" label (e.g. OpenRouter, NVIDIA NIM) - fall back to whatever's actually
        // in the section: an HTML rate-limit table's Model Name column, or a bare bullet list.
        var tableNames = TableRowRegex.Matches(body)
            .Select(x => x.Groups[1].Value.Trim())
            .Where(s => s.Length > 0 && !s.Equals("Model Name", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        if (tableNames.Count > 0) return Truncate(string.Join(", ", tableNames), 300);

        return JoinBullets(body) is { } bullets ? Truncate(bullets, 300) : null;
    }

    private static string? JoinBullets(string text)
    {
        var names = BulletLineRegex.Matches(text)
            .Select(x => CleanMarkdown(x.Groups[1].Value))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(10)
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static string CleanMarkdown(string s)
    {
        s = MdLinkRegex.Replace(s, "$1");
        s = s.Replace("<br>", ", ", StringComparison.OrdinalIgnoreCase).Replace("<br/>", ", ", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("**", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private async Task<int> CheckStalenessAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("explore");
        var flagged = 0;

        foreach (var entry in _kb.FreeApis)
        {
            var (ok, reason) = await CheckUrlAsync(client, entry.SignupUrl, ct);
            if (ok && !string.IsNullOrWhiteSpace(entry.DocsUrl))
                (ok, reason) = await CheckUrlAsync(client, entry.DocsUrl!, ct);

            if (!ok && !entry.NeedsReview)
            {
                await _kb.SetFreeApiReviewFlagAsync(entry.Id, true, reason);
                flagged++;
            }
            else if (ok && entry.NeedsReview)
            {
                await _kb.SetFreeApiReviewFlagAsync(entry.Id, false, null);
            }
        }
        return flagged;
    }

    /// <summary>
    /// A HEAD/GET failure here is a "worth a look" signal, not proof the link is dead - some sites block
    /// automated requests outright regardless of whether a browser could load the page fine, the same
    /// false-positive risk this session already hit scraping Reddit/YouTube. The UI phrases this flag as
    /// "couldn't confirm", not "broken", for exactly that reason.
    /// </summary>
    private static async Task<(bool Ok, string? Reason)> CheckUrlAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LinkCheckTimeout);

            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await client.SendAsync(headReq, cts.Token);
            if (headResp.IsSuccessStatusCode) return (true, null);
            if (headResp.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
                return (false, $"{url} returned {(int)headResp.StatusCode} on automated check");

            using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
            using var getResp = await client.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return getResp.IsSuccessStatusCode ? (true, null) : (false, $"{url} returned {(int)getResp.StatusCode} on automated check");
        }
        catch (Exception ex)
        {
            return (false, $"{url} was unreachable on automated check ({ex.GetType().Name})");
        }
    }
}
