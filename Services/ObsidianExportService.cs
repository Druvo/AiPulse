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
                var tags = string.Join(' ', new[] { b.ContentType, b.Level }.Concat(b.Tags)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => $"#{ToObsidianTag(t)}"));
                var tagSuffix = tags.Length > 0 ? $" {tags}" : "";
                sb.AppendLine($"- [ ] [{EscapeMd(b.Title)}]({b.Link}){src} · saved {b.SavedAt:yyyy-MM-dd}{tagSuffix}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Writes "AiPulse Learning Notes.md" - your own per-module takeaways, not the module content itself (that's static data, not worth exporting). Only modules with a note or a self-check answer are included, so this stays a personal notebook rather than a dump of the whole roadmap.</summary>
    public string ExportLearningNotes(IReadOnlyList<LearningModule> modules)
    {
        var dir = TargetDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AiPulse Learning Notes.md");
        File.WriteAllText(path, BuildLearningMarkdown(modules), Encoding.UTF8);
        return path;
    }

    private string BuildLearningMarkdown(IReadOnlyList<LearningModule> modules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("tags: [ai, learning, aipulse]");
        sb.AppendLine($"updated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# 🎓 AiPulse Learning Notes");
        sb.AppendLine();

        var withContent = modules
            .Where(m => !string.IsNullOrWhiteSpace(_reading.GetModuleNote(m.Title)) || _reading.GetSelfCheck(m.Title) is not null || _reading.IsModuleComplete(m.Title))
            .ToList();

        if (withContent.Count == 0)
        {
            sb.AppendLine("_Nothing to export yet - complete a module or leave yourself a note first._");
            return sb.ToString();
        }

        foreach (var m in withContent)
        {
            sb.AppendLine($"## {EscapeMd(m.Title)}");
            var completedAt = _reading.ModuleCompletedAt.TryGetValue(m.Title, out var at) ? at.ToString("yyyy-MM-dd") : null;
            var status = _reading.IsModuleComplete(m.Title)
                ? (completedAt is not null ? $"✓ completed {completedAt}" : "✓ completed")
                : "in progress";
            sb.AppendLine($"*{status} · {m.Level}*");
            sb.AppendLine();

            var check = _reading.GetSelfCheck(m.Title);
            if (check is not null)
                sb.AppendLine(check == true ? "**Self-check:** could explain this to someone else." : "**Self-check:** not confident yet - worth a refresher.");

            var note = _reading.GetModuleNote(m.Title);
            if (!string.IsNullOrWhiteSpace(note))
            {
                sb.AppendLine();
                sb.AppendLine(note);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string EscapeMd(string s) => s.Replace("[", "(").Replace("]", ")");

    /// <summary>Obsidian tags can't contain spaces or most punctuation; collapse to kebab-case.</summary>
    private static string ToObsidianTag(string s) =>
        string.Join('-', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
