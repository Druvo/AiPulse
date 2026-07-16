using System.Text;
using System.Xml.Linq;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Bulk import/export of feed sources via OPML - the standard interchange format RSS readers use.</summary>
public sealed class OpmlService
{
    private readonly KnowledgeBaseService _kb;
    private readonly IHttpClientFactory _httpFactory;

    public OpmlService(KnowledgeBaseService kb, IHttpClientFactory httpFactory)
    {
        _kb = kb;
        _httpFactory = httpFactory;
    }

    /// <summary>Builds an OPML document from the current sources, grouped by category.</summary>
    public string ExportOpml()
    {
        var body = new XElement("body");
        foreach (var group in _kb.SourceRecords.GroupBy(s => s.Category).OrderBy(g => g.Key))
        {
            var folder = new XElement("outline", new XAttribute("text", group.Key), new XAttribute("title", group.Key));
            foreach (var s in group.OrderBy(s => s.Name))
            {
                folder.Add(new XElement("outline",
                    new XAttribute("text", s.Name),
                    new XAttribute("title", s.Name),
                    new XAttribute("type", "rss"),
                    new XAttribute("xmlUrl", s.Url)));
            }
            body.Add(folder);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "AiPulse Sources")),
                body));

        using var ms = new MemoryStream();
        doc.Save(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Parses an OPML file and adds any feed URLs not already present. Category comes from the enclosing
    /// folder outline's title if present, otherwise defaults to "News". Every new URL gets a quick
    /// reachability check so a bad import doesn't silently add dead links - unreachable ones are still
    /// added (a feed can be down momentarily) but counted separately so the admin can review them.
    /// <paramref name="onProgress"/> is optional and reports Done/Total as each URL's reachability check
    /// completes - this is the slow part for a large import, so it's the only phase worth reporting on.
    /// </summary>
    public async Task<(int Added, int Skipped, int Unreachable)> ImportOpmlAsync(Stream opmlStream, Func<OpmlImportProgress, Task>? onProgress = null)
    {
        var doc = await XDocument.LoadAsync(opmlStream, LoadOptions.None, default);
        var existingUrls = new HashSet<string>(_kb.SourceRecords.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<(string Name, string Url, string? Category)>();
        int skipped = 0;
        foreach (var entry in WalkOutlines(doc.Descendants("body").FirstOrDefault(), null))
        {
            if (string.IsNullOrWhiteSpace(entry.Url) || existingUrls.Contains(entry.Url))
            {
                skipped++;
                continue;
            }
            existingUrls.Add(entry.Url); // guard against duplicate entries within the same OPML file
            toAdd.Add(entry);
        }

        var reachability = await CheckReachabilityAsync(toAdd, onProgress);

        var added = 0;
        var unreachable = 0;
        foreach (var (name, url, category) in toAdd)
        {
            await _kb.AddSourceAsync(new SourceRecord
            {
                Name = string.IsNullOrWhiteSpace(name) ? url : name,
                Url = url,
                Category = category ?? "News",
                Enabled = true,
                ContentType = "News",
                Level = "Intermediate"
            });
            added++;
            if (!reachability.GetValueOrDefault(url, true))
                unreachable++;
        }

        return (added, skipped, unreachable);
    }

    /// <summary>Quick, concurrency-limited GET per URL (5s timeout each) - just enough to flag dead links, not a full fetch.</summary>
    private async Task<Dictionary<string, bool>> CheckReachabilityAsync(
        List<(string Name, string Url, string? Category)> entries, Func<OpmlImportProgress, Task>? onProgress)
    {
        var client = _httpFactory.CreateClient("feeds");
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        using var gate = new SemaphoreSlim(5);
        var total = entries.Count;
        var done = 0;

        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var resp = await client.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                results[entry.Url] = resp.IsSuccessStatusCode;
            }
            catch
            {
                results[entry.Url] = false;
            }
            finally
            {
                gate.Release();
                if (onProgress is not null)
                {
                    var n = Interlocked.Increment(ref done);
                    await onProgress(new OpmlImportProgress { Done = n, Total = total, CurrentName = entry.Name });
                }
            }
        });

        await Task.WhenAll(tasks);
        return new Dictionary<string, bool>(results);
    }

    /// <summary>Recursively walks outline elements; a folder (no xmlUrl) sets the category for its children.</summary>
    private static IEnumerable<(string Name, string Url, string? Category)> WalkOutlines(XElement? parent, string? inheritedCategory)
    {
        if (parent is null) yield break;

        foreach (var outline in parent.Elements("outline"))
        {
            var xmlUrl = (string?)outline.Attribute("xmlUrl");
            var title = (string?)outline.Attribute("title") ?? (string?)outline.Attribute("text") ?? "";

            if (!string.IsNullOrWhiteSpace(xmlUrl))
            {
                yield return (title, xmlUrl.Trim(), inheritedCategory);
            }
            else
            {
                // Folder outline - its title becomes the category for children, if one of our known categories.
                var category = MapToCategory(title);
                foreach (var child in WalkOutlines(outline, category ?? inheritedCategory))
                    yield return child;
            }
        }
    }

    private static readonly string[] KnownCategories = { "News", "Research", "Tools", "Community" };

    private static string? MapToCategory(string folderTitle) =>
        KnownCategories.FirstOrDefault(c => c.Equals(folderTitle, StringComparison.OrdinalIgnoreCase));
}
