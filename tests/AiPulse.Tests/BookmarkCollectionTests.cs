using AiPulse.Models;
using AiPulse.Services.Productivity;
using Xunit;

namespace AiPulse.Tests;

public class BookmarkCollectionTests
{
    private static (BookmarkCollectionService Svc, TestProductivityDb Db) Make()
    {
        var db = new TestProductivityDb();
        return (new BookmarkCollectionService(db.Db, new FakeClock(Items.Now)), db);
    }

    private static BookmarkItem B(string title, string link, string source = "Lab") =>
        new() { Title = title, Link = link, SourceName = source, SavedAt = Items.Now };

    [Fact]
    public void Tags_are_trimmed_deduped_and_capped()
    {
        Assert.Equal(new[] { "AI", "agents", "MCP" }, BookmarkCollectionLogic.NormaliseTags("AI,  agents, ai ; #MCP,,"));
        Assert.Equal(BookmarkCollectionLogic.MaxTags, BookmarkCollectionLogic.NormaliseTags(string.Join(',', Enumerable.Range(0, 40).Select(i => "t" + i))).Count);
        Assert.Equal(BookmarkCollectionLogic.MaxTagLength, BookmarkCollectionLogic.NormaliseTags(new string('x', 100))[0].Length);
        Assert.Empty(BookmarkCollectionLogic.NormaliseTags(null));
    }

    [Fact]
    public void Filter_matches_title_source_tags_and_note_and_hash_means_exact_tag()
    {
        var b = B("Prompt caching deep dive", "https://x.test/1", "Anthropic");
        var note = new BookmarkAnnotation { Tags = "perf, llm", Note = "Try this at work" };
        Assert.True(BookmarkCollectionLogic.MatchesFilter(b, note, "caching"));
        Assert.True(BookmarkCollectionLogic.MatchesFilter(b, note, "anthropic"));
        Assert.True(BookmarkCollectionLogic.MatchesFilter(b, note, "at work"));
        Assert.True(BookmarkCollectionLogic.MatchesFilter(b, note, "#LLM"));
        Assert.False(BookmarkCollectionLogic.MatchesFilter(b, note, "#ll"));
        Assert.True(BookmarkCollectionLogic.MatchesFilter(b, null, ""));
    }

    [Fact]
    public void Markdown_export_lists_links_tags_and_notes_and_escapes_titles()
    {
        var bookmarks = new[] { B("Weird [title] *bold*", "https://x.test/a (1)"), B("Plain", "https://x.test/b", source: "") };
        var notes = new Dictionary<string, BookmarkAnnotation>
        {
            ["https://x.test/a (1)"] = new() { Tags = "rag, long context", Note = "line one\r\nline two" }
        };
        var md = BookmarkCollectionLogic.ExportMarkdown("Reading [list]", bookmarks, notes, Items.Now);
        Assert.StartsWith("# Reading \\[list\\]", md);
        Assert.Contains("_Exported from AiPulse on 2026-11-05 · 2 items_", md);
        Assert.Contains("- [Weird \\[title\\] \\*bold\\*](https://x.test/a%20%281%29) — Lab · saved 2026-11-05", md);
        Assert.Contains("  #rag #long-context", md);
        Assert.Contains("  > line one", md);
        Assert.Contains("  > line two", md);
        Assert.Contains("- [Plain](https://x.test/b) — saved 2026-11-05", md);
    }

    [Fact]
    public void File_names_are_safe_slugs()
    {
        Assert.Equal("my-ai-reading.md", BookmarkCollectionLogic.FileName("My AI / Reading!"));
        Assert.Equal("collection.md", BookmarkCollectionLogic.FileName("???"));
    }

    [Fact]
    public async Task Collections_are_created_per_user_with_unique_names()
    {
        var (svc, db) = Make();
        using var _ = db;
        var (c, err) = await svc.CreateAsync(1, "  Research ");
        Assert.Null(err);
        Assert.Equal("Research", c!.Name);
        Assert.NotNull((await svc.CreateAsync(1, "research")).Error);
        Assert.NotNull((await svc.CreateAsync(1, " ")).Error);
        Assert.Null((await svc.CreateAsync(2, "Research")).Error);
        Assert.Single(await svc.ListAsync(1));
    }

    [Fact]
    public async Task Copy_move_and_remove_bookmarks_between_collections()
    {
        var (svc, db) = Make();
        using var _ = db;
        var a = (await svc.CreateAsync(1, "A")).Collection!;
        var b = (await svc.CreateAsync(1, "B")).Collection!;
        const string link = "https://x.test/1";

        Assert.True(await svc.AddAsync(1, a.Id, link));
        Assert.True(await svc.AddAsync(1, a.Id, link)); // idempotent
        Assert.True(await svc.AddAsync(1, b.Id, link)); // copy
        var m = await svc.MembershipAsync(1);
        Assert.Contains(link, m[a.Id]);
        Assert.Contains(link, m[b.Id]);

        Assert.True(await svc.RemoveAsync(1, b.Id, link));
        Assert.True(await svc.MoveAsync(1, a.Id, b.Id, link));
        m = await svc.MembershipAsync(1);
        Assert.False(m.ContainsKey(a.Id));
        Assert.Contains(link, m[b.Id]);
    }

    [Fact]
    public async Task Users_cannot_touch_each_others_collections()
    {
        var (svc, db) = Make();
        using var _ = db;
        var mine = (await svc.CreateAsync(1, "Mine")).Collection!;
        Assert.False(await svc.AddAsync(2, mine.Id, "https://x.test/1"));
        Assert.False(await svc.DeleteAsync(2, mine.Id));
        Assert.NotNull(await svc.RenameAsync(2, mine.Id, "Stolen"));
        Assert.Equal("Mine", (await svc.ListAsync(1))[0].Name);
    }

    [Fact]
    public async Task Rename_and_delete_keep_the_bookmarks_notes()
    {
        var (svc, db) = Make();
        using var _ = db;
        var c = (await svc.CreateAsync(1, "Old")).Collection!;
        await svc.AddAsync(1, c.Id, "https://x.test/1");
        await svc.SetAnnotationAsync(1, "https://x.test/1", "a", "note");
        Assert.Null(await svc.RenameAsync(1, c.Id, "New"));
        Assert.Equal("New", (await svc.ListAsync(1))[0].Name);
        Assert.True(await svc.DeleteAsync(1, c.Id));
        Assert.Empty(await svc.MembershipAsync(1));
        Assert.Equal("note", (await svc.AnnotationsAsync(1))["https://x.test/1"].Note);
    }

    [Fact]
    public async Task Annotations_are_private_normalised_and_removed_when_cleared()
    {
        var (svc, db) = Make();
        using var _ = db;
        var saved = await svc.SetAnnotationAsync(1, "https://x.test/1", "B, b, #c", "  my note  ");
        Assert.Equal("B, c", saved!.Tags);
        Assert.Equal("my note", saved.Note);
        Assert.Empty(await svc.AnnotationsAsync(2));
        await svc.SetAnnotationAsync(1, "https://x.test/1", "", "");
        Assert.Empty(await svc.AnnotationsAsync(1));
        var big = await svc.SetAnnotationAsync(1, "https://x.test/2", null, new string('n', 10_000));
        Assert.Equal(BookmarkCollectionLogic.MaxNoteLength, big!.Note.Length);
    }
}
