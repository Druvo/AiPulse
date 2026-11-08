using System.Text;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services.Productivity;

/// <summary>Pure helpers for bookmark collections: tag normalisation, filtering and the Markdown export.</summary>
public static class BookmarkCollectionLogic
{
    public const int MaxTags = 12;
    public const int MaxTagLength = 32;
    public const int MaxNoteLength = 4000;
    public const int MaxCollectionName = 60;

    /// <summary>"AI,  agents, ai , #MCP" → ["AI", "agents", "MCP"]: trimmed, '#' dropped, case-insensitively distinct, capped.</summary>
    public static IReadOnlyList<string> NormaliseTags(string? raw) =>
        (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Trim())
            .Where(t => t.Length > 0)
            .Select(t => t.Length > MaxTagLength ? t[..MaxTagLength] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();

    public static string JoinTags(IEnumerable<string> tags) => string.Join(", ", tags);

    /// <summary>Bookmarks matching a free-text filter over title, source, the user's tags and their note.</summary>
    public static bool MatchesFilter(BookmarkItem b, BookmarkAnnotation? note, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        if (f.StartsWith('#'))
            return NormaliseTags(note?.Tags).Any(t => string.Equals(t, f[1..], StringComparison.OrdinalIgnoreCase));
        return b.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.SourceName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || (note?.Tags ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
            || (note?.Note ?? "").Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Markdown export of a collection: a heading, then one list entry per bookmark with its link, source, save date, the user's tags
    /// and (indented as a quote) their note. Markdown-significant characters in titles are escaped so a title cannot break the list.
    /// </summary>
    public static string ExportMarkdown(string collectionName, IEnumerable<BookmarkItem> bookmarks, IReadOnlyDictionary<string, BookmarkAnnotation> notes, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(EscapeInline(collectionName));
        sb.AppendLine();
        var list = bookmarks.ToList();
        sb.AppendLine($"_Exported from AiPulse on {exportedAt:yyyy-MM-dd} · {list.Count} item{(list.Count == 1 ? "" : "s")}_");
        sb.AppendLine();
        foreach (var b in list)
        {
            sb.Append("- [").Append(EscapeInline(b.Title)).Append("](").Append(EscapeUrl(b.Link)).Append(')');
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(b.SourceName)) meta.Add(EscapeInline(b.SourceName));
            meta.Add($"saved {b.SavedAt:yyyy-MM-dd}");
            sb.Append(" — ").AppendLine(string.Join(" · ", meta));
            notes.TryGetValue(b.Link, out var n);
            var tags = NormaliseTags(n?.Tags);
            if (tags.Count > 0)
                sb.Append("  ").AppendLine(string.Join(' ', tags.Select(t => "#" + t.Replace(' ', '-'))));
            if (!string.IsNullOrWhiteSpace(n?.Note))
                foreach (var line in n.Note.Replace("\r", "").Split('\n'))
                    sb.Append("  > ").AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>A safe file name for the download: letters, digits, dashes.</summary>
    public static string FileName(string collectionName)
    {
        var slug = new string(collectionName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return (slug.Length == 0 ? "collection" : slug.Length > 50 ? slug[..50] : slug) + ".md";
    }

    private static string EscapeInline(string s) =>
        new StringBuilder(s.Replace("\r", " ").Replace("\n", " "))
            .Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`")
            .ToString();

    private static string EscapeUrl(string url) => url.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
}

/// <summary>Collections, membership and per-bookmark tags/notes for one user. Bookmarks themselves stay in ReadingStateService.</summary>
public sealed class BookmarkCollectionService
{
    public const int MaxCollectionsPerUser = 100;
    private readonly ProductivityDb _db;
    private readonly TimeProvider _time;

    public BookmarkCollectionService(ProductivityDb db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<List<BookmarkCollection>> ListAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var list = await db.BookmarkCollections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct);
        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<(BookmarkCollection? Collection, string? Error)> CreateAsync(int userId, string name, CancellationToken ct = default)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) return (null, "Give the collection a name.");
        if (clean.Length > BookmarkCollectionLogic.MaxCollectionName) return (null, $"Keep the name under {BookmarkCollectionLogic.MaxCollectionName} characters.");
        await using var db = await _db.OpenAsync(ct);
        var existing = await db.BookmarkCollections.Where(c => c.UserId == userId).Select(c => c.Name).ToListAsync(ct);
        if (existing.Count >= MaxCollectionsPerUser) return (null, $"You can keep up to {MaxCollectionsPerUser} collections.");
        if (existing.Any(n => string.Equals(n, clean, StringComparison.OrdinalIgnoreCase))) return (null, "You already have a collection with that name.");
        var c = new BookmarkCollection { UserId = userId, Name = clean, CreatedUtc = _time.GetUtcNow().UtcDateTime };
        db.BookmarkCollections.Add(c);
        await db.SaveChangesAsync(ct);
        return (c, null);
    }

    public async Task<string?> RenameAsync(int userId, int collectionId, string name, CancellationToken ct = default)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0 || clean.Length > BookmarkCollectionLogic.MaxCollectionName) return "Enter a name up to 60 characters.";
        await using var db = await _db.OpenAsync(ct);
        var c = await db.BookmarkCollections.FirstOrDefaultAsync(x => x.Id == collectionId && x.UserId == userId, ct);
        if (c is null) return "That collection no longer exists.";
        var clash = (await db.BookmarkCollections.Where(x => x.UserId == userId && x.Id != collectionId).Select(x => x.Name).ToListAsync(ct))
            .Any(n => string.Equals(n, clean, StringComparison.OrdinalIgnoreCase));
        if (clash) return "You already have a collection with that name.";
        c.Name = clean;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Deletes the collection and its memberships. The bookmarks themselves (and their notes) are kept.</summary>
    public async Task<bool> DeleteAsync(int userId, int collectionId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var c = await db.BookmarkCollections.FirstOrDefaultAsync(x => x.Id == collectionId && x.UserId == userId, ct);
        if (c is null) return false;
        db.BookmarkCollectionEntries.RemoveRange(db.BookmarkCollectionEntries.Where(e => e.CollectionId == collectionId && e.UserId == userId));
        db.BookmarkCollections.Remove(c);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Collection id → links in it, for all of the user's collections.</summary>
    public async Task<Dictionary<int, HashSet<string>>> MembershipAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rows = await db.BookmarkCollectionEntries.AsNoTracking().Where(e => e.UserId == userId).Select(e => new { e.CollectionId, e.Link }).ToListAsync(ct);
        return rows.GroupBy(r => r.CollectionId).ToDictionary(g => g.Key, g => g.Select(r => r.Link).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Copies a bookmark into a collection (no-op if already there). False if the collection is not the user's.</summary>
    public async Task<bool> AddAsync(int userId, int collectionId, string link, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        if (!await db.BookmarkCollections.AnyAsync(c => c.Id == collectionId && c.UserId == userId, ct)) return false;
        if (await db.BookmarkCollectionEntries.AnyAsync(e => e.CollectionId == collectionId && e.Link == link, ct)) return true;
        db.BookmarkCollectionEntries.Add(new BookmarkCollectionEntry { CollectionId = collectionId, UserId = userId, Link = link, AddedUtc = _time.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(int userId, int collectionId, string link, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rows = await db.BookmarkCollectionEntries.Where(e => e.CollectionId == collectionId && e.UserId == userId && e.Link == link).ToListAsync(ct);
        if (rows.Count == 0) return false;
        db.BookmarkCollectionEntries.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Moves a bookmark from one collection to another (from = null means "from wherever it is filed": all its collections).</summary>
    public async Task<bool> MoveAsync(int userId, int? fromCollectionId, int toCollectionId, string link, CancellationToken ct = default)
    {
        if (fromCollectionId == toCollectionId) return true;
        if (!await AddAsync(userId, toCollectionId, link, ct)) return false;
        await using var db = await _db.OpenAsync(ct);
        var q = db.BookmarkCollectionEntries.Where(e => e.UserId == userId && e.Link == link && e.CollectionId != toCollectionId);
        if (fromCollectionId is { } from) q = q.Where(e => e.CollectionId == from);
        db.BookmarkCollectionEntries.RemoveRange(await q.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Dictionary<string, BookmarkAnnotation>> AnnotationsAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await _db.OpenAsync(ct);
        var rows = await db.BookmarkAnnotations.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        return rows.GroupBy(r => r.Link).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>Saves the user's tags and private note on a bookmark. Empty tags and note remove the row.</summary>
    public async Task<BookmarkAnnotation?> SetAnnotationAsync(int userId, string link, string? tags, string? note, CancellationToken ct = default)
    {
        var cleanTags = BookmarkCollectionLogic.JoinTags(BookmarkCollectionLogic.NormaliseTags(tags));
        var cleanNote = (note ?? "").Trim();
        if (cleanNote.Length > BookmarkCollectionLogic.MaxNoteLength) cleanNote = cleanNote[..BookmarkCollectionLogic.MaxNoteLength];
        await using var db = await _db.OpenAsync(ct);
        var row = await db.BookmarkAnnotations.FirstOrDefaultAsync(a => a.UserId == userId && a.Link == link, ct);
        if (cleanTags.Length == 0 && cleanNote.Length == 0)
        {
            if (row is not null) { db.BookmarkAnnotations.Remove(row); await db.SaveChangesAsync(ct); }
            return null;
        }
        row ??= db.BookmarkAnnotations.Add(new BookmarkAnnotation { UserId = userId, Link = link }).Entity;
        row.Tags = cleanTags;
        row.Note = cleanNote;
        row.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return row;
    }
}
