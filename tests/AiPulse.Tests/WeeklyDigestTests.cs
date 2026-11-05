using System.Xml.Linq;
using AiPulse.Models;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests;

public class WeeklyDigestTests
{
    private static readonly DateTimeOffset Now = Items.Now;

    [Fact]
    public void An_empty_history_gives_an_empty_digest()
    {
        var d = WeeklyDigest.LastSevenDays(Array.Empty<FeedItem>(), Now);
        Assert.True(d.IsEmpty);
        Assert.Empty(d.TopTopics);
        Assert.Empty(d.MostCovered);
        Assert.Empty(d.NewReleases);
        Assert.Equal(0, d.StoryCount);
        Assert.Contains("quiet week", WeeklyDigest.Summary(d));
    }

    [Fact]
    public void Only_the_last_seven_days_count()
    {
        var items = new[]
        {
            Items.Make("in", published: Now.AddDays(-6)),
            Items.Make("out", published: Now.AddDays(-8)),
            Items.Make("future", published: Now.AddDays(1)),
        };
        var d = WeeklyDigest.LastSevenDays(items, Now);
        Assert.Equal(1, d.StoryCount);
    }

    [Fact]
    public void Most_covered_ranks_by_distinct_sources_and_ignores_single_source_stories()
    {
        var items = new[]
        {
            Items.Make("Two sources", source: "A", alsoSeenOn: new[] { "B" }),
            Items.Make("Three sources", source: "A", alsoSeenOn: new[] { "B", "C", "c", "A" }),
            Items.Make("Alone", source: "A"),
        };
        var d = WeeklyDigest.LastSevenDays(items, Now);
        Assert.Equal(new[] { "Three sources", "Two sources" }, d.MostCovered.Select(s => s.Item.Title));
        Assert.Equal(3, d.MostCovered[0].Coverage);
        Assert.Equal(new[] { "A", "B", "C" }, d.MostCovered[0].Sources);
    }

    [Fact]
    public void Coverage_ties_break_by_newest_then_title_then_link()
    {
        var t = Now.AddHours(-5);
        var items = new[]
        {
            Items.Make("Older", alsoSeenOn: new[] { "B" }, published: t.AddHours(-1)),
            Items.Make("beta", link: "https://x.test/2", alsoSeenOn: new[] { "B" }, published: t),
            Items.Make("Alpha", link: "https://x.test/3", alsoSeenOn: new[] { "B" }, published: t),
            Items.Make("alpha", link: "https://x.test/1", alsoSeenOn: new[] { "B" }, published: t),
        };
        var d = WeeklyDigest.LastSevenDays(items, Now);
        Assert.Equal(new[] { "https://x.test/1", "https://x.test/3", "https://x.test/2" }, d.MostCovered.Take(3).Select(s => s.Item.Link));
        Assert.Equal("Older", d.MostCovered[3].Item.Title);
    }

    [Fact]
    public void Topics_rank_by_count_with_alphabetical_ties_and_need_two_stories()
    {
        var items = new[]
        {
            Items.Make("1", tags: new[] { "Agents", "RAG" }),
            Items.Make("2", tags: new[] { "agents", "Vision" }),
            Items.Make("3", tags: new[] { "RAG", "Vision" }),
            Items.Make("4", tags: new[] { "Agents", "Solo" }),
        };
        var d = WeeklyDigest.LastSevenDays(items, Now);
        Assert.Equal(new[] { "Agents", "RAG", "Vision" }, d.TopTopics.Select(t => t.Tag));
        Assert.Equal(3, d.TopTopics[0].Count);
    }

    [Fact]
    public void Releases_keep_the_newest_per_source_and_count_the_rest()
    {
        var items = new[]
        {
            Items.Make("lib v1", source: "Lib", contentType: "Release", published: Now.AddDays(-3)),
            Items.Make("lib v2", source: "Lib", contentType: "Release", published: Now.AddDays(-1)),
            Items.Make("tool v9", source: "tool", contentType: "Release", published: Now.AddDays(-2)),
            Items.Make("Tool v10", source: "Tool", contentType: "Release", published: Now.AddDays(-2)),
        };
        var d = WeeklyDigest.LastSevenDays(items, Now);
        Assert.Equal(new[] { "lib v2" }, d.NewReleases.Take(1).Select(r => r.Latest.Title));
        Assert.Equal(2, d.NewReleases[0].Count);
        Assert.Equal(2, d.NewReleases.Count);
        Assert.Equal(2, d.NewReleases[1].Count);
        Assert.Equal(0, d.StoryCount); // releases are not headlines
        Assert.False(d.IsEmpty);
    }

    [Fact]
    public void Duplicate_links_are_counted_once()
    {
        var a = Items.Make("dup", link: "https://x.test/d", alsoSeenOn: new[] { "B" });
        var d = WeeklyDigest.LastSevenDays(new[] { a, a }, Now);
        Assert.Equal(1, d.StoryCount);
        Assert.Single(d.MostCovered);
    }

    [Fact]
    public void Limits_are_respected()
    {
        var items = Enumerable.Range(0, 30).Select(i => Items.Make("s" + i, alsoSeenOn: new[] { "B" }, published: Now.AddMinutes(-i))).ToList();
        var d = WeeklyDigest.Build(items, Now.AddDays(-7), Now, stories: 5);
        Assert.Equal(5, d.MostCovered.Count);
        Assert.Equal(30, d.StoryCount);
    }

    [Fact]
    public void Week_start_is_monday_utc_and_labels_are_iso_weeks()
    {
        var thursday = new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.FromHours(-5)); // Friday 04:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), WeeklyDigest.WeekStart(thursday));
        Assert.Equal("2026-W45", WeeklyDigest.WeekLabel(WeeklyDigest.WeekStart(thursday)));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), WeeklyDigest.WeekStart(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), WeeklyDigest.WeekStart(new DateTimeOffset(2026, 11, 8, 23, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Atom_feed_has_one_entry_per_non_empty_week_with_stable_ids()
    {
        var items = new[]
        {
            Items.Make("this week", alsoSeenOn: new[] { "B" }, published: Now.AddHours(-2)),
            Items.Make("last week", published: Now.AddDays(-7)),
            Items.Make("ancient", published: Now.AddDays(-60)),
        };
        var xml = WeeklyDigest.Atom(items, "https://example.test", Now);
        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entries = doc.Root!.Elements(atom + "entry").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("urn:aipulse:this-week:2026-W45", entries[0].Element(atom + "id")!.Value);
        Assert.Equal("urn:aipulse:this-week:2026-W44", entries[1].Element(atom + "id")!.Value);
        Assert.Contains("this week", entries[0].Element(atom + "content")!.Value);
        Assert.Equal("https://example.test/this-week.xml", doc.Root.Element(atom + "id")!.Value);
    }

    [Fact]
    public void Atom_feed_for_an_empty_history_is_valid_and_has_no_entries()
    {
        var doc = XDocument.Parse(WeeklyDigest.Atom(Array.Empty<FeedItem>(), "https://example.test", Now));
        Assert.Empty(doc.Root!.Elements(XName.Get("entry", "http://www.w3.org/2005/Atom")));
    }

    [Fact]
    public void Html_rendering_encodes_text_and_refuses_script_links()
    {
        var evil = Items.Make("<script>alert(1)</script>", link: "javascript:alert(1)", alsoSeenOn: new[] { "B" });
        var html = WeeklyDigest.ToHtmlFragment(WeeklyDigest.LastSevenDays(new[] { evil }, Now), "https://example.test");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void Plain_text_lists_stories_topics_releases_and_the_unsubscribe_link()
    {
        var items = new[]
        {
            Items.Make("Big story", alsoSeenOn: new[] { "B" }, tags: new[] { "LLM" }),
            Items.Make("Other", tags: new[] { "LLM" }),
            Items.Make("v2", source: "Lib", contentType: "Release"),
        };
        var text = WeeklyDigest.ToPlainText(WeeklyDigest.LastSevenDays(items, Now), "https://example.test", "https://example.test/u?t=1");
        Assert.Contains("Big story (2 sources)", text);
        Assert.Contains("LLM (2)", text);
        Assert.Contains("Lib: v2", text);
        Assert.Contains("https://example.test/this-week", text);
        Assert.Contains("Unsubscribe: https://example.test/u?t=1", text);
    }

    [Fact]
    public void Range_text_spans_months_cleanly()
    {
        Assert.Equal("Nov 1–8, 2026", WeeklyDigest.RangeText(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Oct 29 – Nov 5, 2026", WeeklyDigest.RangeText(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), Now));
    }
}
