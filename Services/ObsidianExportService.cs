using System.Text;
using AiPulse.Models;

namespace AiPulse.Services;

/// <summary>Writes the reading list to a Markdown note (designed to drop straight into an Obsidian vault).</summary>
public sealed class ObsidianExportService
{
    private readonly ReadingStateService _reading;
    private readonly string _fallbackDir;

    public ObsidianExportService(ReadingStateService reading, IWebHostEnvironment env)
    {
        _reading = reading;
        _fallbackDir = Path.Combine(env.ContentRootPath, "App_Data", "exports");
    }

    /// <summary>The folder exports are written to (user-configured path, or the local fallback).</summary>
    public string TargetDir =>
        string.IsNullOrWhiteSpace(_reading.ObsidianExportPath) ? _fallbackDir : _reading.ObsidianExportPath!;

    /// <summary>Writes "AiPulse Reading List.md" to the target folder and returns the full file path.</summary>
    public string ExportReadingList()
    {
        var dir = TargetDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AiPulse Reading List.md");
        File.WriteAllText(path, BuildMarkdown(_reading.Bookmarks), Encoding.UTF8);
        return path;
    }

    private static string BuildMarkdown(IReadOnlyList<BookmarkItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("tags: [ai, reading-list, aipulse]");
        sb.AppendLine($"updated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# 🔖 AiPulse Reading List");
        sb.AppendLine();
        sb.AppendLine($"Exported from AiPulse on {DateTimeOffset.Now:f}. {items.Count} saved article(s).");
        sb.AppendLine();

        if (items.Count == 0)
        {
            sb.AppendLine("_Nothing saved yet._");
            return sb.ToString();
        }

        foreach (var group in items.GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
                                    .OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var b in group.OrderByDescending(b => b.SavedAt))
            {
                var src = string.IsNullOrWhiteSpace(b.SourceName) ? "" : $" — _{b.SourceName}_";
                sb.AppendLine($"- [ ] [{EscapeMd(b.Title)}]({b.Link}){src} · saved {b.SavedAt:yyyy-MM-dd}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string EscapeMd(string s) => s.Replace("[", "(").Replace("]", ")");
}
