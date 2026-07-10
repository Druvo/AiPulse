using System.Text;
using System.Xml.Linq;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Bulk import/export of feed sources via OPML - the standard interchange format RSS readers use.</summary>
public sealed class OpmlService
{
    private readonly KnowledgeBaseService _kb;

    public OpmlService(KnowledgeBaseService kb) => _kb = kb;

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
    /// folder outline's title if present, otherwise defaults to "News". Returns (added, skipped-as-duplicate).
    /// </summary>
    public async Task<(int Added, int Skipped)> ImportOpmlAsync(Stream opmlStream)
    {
        var doc = await XDocument.LoadAsync(opmlStream, LoadOptions.None, default);
        var existingUrls = new HashSet<string>(_kb.SourceRecords.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);

        int added = 0, skipped = 0;
        foreach (var (name, url, category) in WalkOutlines(doc.Descendants("body").FirstOrDefault(), null))
        {
            if (string.IsNullOrWhiteSpace(url) || existingUrls.Contains(url))
            {
                skipped++;
                continue;
            }

            await _kb.AddSourceAsync(new SourceRecord
            {
                Name = string.IsNullOrWhiteSpace(name) ? url : name,
                Url = url,
                Category = category ?? "News",
                Enabled = true,
                ContentType = "News",
                Level = "Intermediate"
            });
            existingUrls.Add(url);
            added++;
        }

        return (added, skipped);
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
