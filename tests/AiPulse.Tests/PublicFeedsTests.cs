using System.Xml.Linq;
using AiPulse.Models;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests;

public class PublicFeedsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static FeedItem Item(string title, string[]? tags = null, int hoursAgo = 1, string type = "News", string summary = "") => new()
    {
        Title = title,
        Link = "https://example.test/" + Uri.EscapeDataString(title),
        Published = Now.AddHours(-hoursAgo),
        SourceName = "Example",
        Category = "News",
        ContentType = type,
        Tags = tags ?? Array.Empty<string>(),
        Summary = summary
    };

    [Fact]
    public void Topics_count_tags_ignoring_case_and_show_the_most_common_spelling()
    {
        var items = new[]
        {
            Item("a", new[] { "RAG" }), Item("b", new[] { "RAG" }), Item("c", new[] { "rag" }),
            Item("d", new[] { "agents" }), Item("e", new[] { "Agents" }), Item("f", new[] { "agents" })
        };

        var topics = PublicFeeds.Topics(items, Now.AddDays(-30));

        Assert.Equal(new[] { "agents", "RAG" }, topics.Select(t => t.Tag)); // tied at 3: alphabetical, and "agents" is the commoner spelling
        Assert.All(topics, t => Assert.Equal(3, t.Count));
    }

    [Fact]
    public void Topics_skip_old_items_releases_and_one_off_tags()
    {
        var items = new[]
        {
            Item("old1", new[] { "ancient" }, hoursAgo: 24 * 60), Item("old2", new[] { "ancient" }, hoursAgo: 24 * 60),
            Item("rel1", new[] { "v2" }, type: "Release"), Item("rel2", new[] { "v2" }, type: "Release"),
            Item("lonely", new[] { "once" })
        };

        Assert.Empty(PublicFeeds.Topics(items, Now.AddDays(-30)));
    }

    [Fact]
    public void A_tag_counts_once_per_item_even_when_it_is_repeated()
    {
        var items = new[] { Item("a", new[] { "RAG", "rag", "RAG " }), Item("b", new[] { "RAG" }) };
        Assert.Equal(2, PublicFeeds.Topics(items, Now.AddDays(-30)).Single().Count);
    }

    [Fact]
    public void ForTag_matches_case_insensitively_newest_first_and_leaves_releases_out()
    {
        var items = new[]
        {
            Item("older", new[] { "Agents" }, hoursAgo: 10), Item("newer", new[] { "agents" }, hoursAgo: 2),
            Item("release", new[] { "agents" }, type: "Release"), Item("other", new[] { "rag" })
        };

        Assert.Equal(new[] { "newer", "older" }, PublicFeeds.ForTag(items, "AGENTS").Select(i => i.Title));
    }

    [Fact]
    public void Releases_are_only_releases_newest_first_and_never_from_the_future()
    {
        var items = new[]
        {
            Item("headline"), Item("r-old", type: "Release", hoursAgo: 30), Item("r-new", type: "Release", hoursAgo: 3),
            Item("r-future", type: "Release", hoursAgo: -5)
        };

        Assert.Equal(new[] { "r-new", "r-old" }, PublicFeeds.Releases(items, Now).Select(i => i.Title));
    }

    [Fact]
    public void Atom_is_well_formed_escapes_markup_and_carries_the_fields_readers_need()
    {
        var items = new[] { Item("Fish & <chips> \"2\"", new[] { "rag" }, summary: "<p>Hello <b>world</b> &amp; friends</p>") };

        var xml = PublicFeeds.Atom(items, "https://example.test", "AiPulse - latest AI news", "/feed.xml", Now);
        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";

        Assert.Equal("feed", doc.Root!.Name.LocalName);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var entry = doc.Root.Element(atom + "entry")!;
        Assert.Equal("Fish & <chips> \"2\"", entry.Element(atom + "title")!.Value);
        Assert.Equal("Hello world & friends", entry.Element(atom + "summary")!.Value);
        Assert.Equal(items[0].Link, entry.Element(atom + "link")!.Attribute("href")!.Value);
        Assert.Contains(entry.Elements(atom + "category").Select(c => c.Attribute("term")!.Value), t => t == "rag");
        Assert.Equal("https://example.test/feed.xml", doc.Root.Elements(atom + "link").Single(l => l.Attribute("rel")!.Value == "self").Attribute("href")!.Value);
    }

    [Fact]
    public void Atom_entry_ids_are_stable_and_distinct_and_future_dates_are_clamped()
    {
        var items = new[] { Item("one"), Item("two", hoursAgo: -48) };
        XNamespace atom = "http://www.w3.org/2005/Atom";

        var doc = XDocument.Parse(PublicFeeds.Atom(items, "https://example.test", "t", "/feed.xml", Now));
        var ids = doc.Root!.Elements(atom + "entry").Select(e => e.Element(atom + "id")!.Value).ToList();

        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(PublicFeeds.EntryId(items[0].Link), ids[0]);
        Assert.All(doc.Root.Elements(atom + "entry"), e => Assert.True(DateTimeOffset.Parse(e.Element(atom + "updated")!.Value) <= Now));
        Assert.True(DateTimeOffset.Parse(doc.Root.Element(atom + "updated")!.Value) <= Now);
    }

    [Fact]
    public void An_empty_feed_is_still_valid_and_the_feed_is_capped()
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        Assert.Empty(XDocument.Parse(PublicFeeds.Atom(Array.Empty<FeedItem>(), "https://x.test", "t", "/feed.xml", Now)).Root!.Elements(atom + "entry"));

        var many = Enumerable.Range(0, 120).Select(i => Item("n" + i, hoursAgo: i + 1));
        Assert.Equal(PublicFeeds.FeedLimit, XDocument.Parse(PublicFeeds.Atom(many, "https://x.test", "t", "/feed.xml", Now)).Root!.Elements(atom + "entry").Count());
    }

    [Fact]
    public void Excerpt_strips_html_collapses_space_and_truncates_with_an_ellipsis()
    {
        Assert.Equal("a b", PublicFeeds.Excerpt("<div>a\n\n   <i>b</i></div>"));
        var cut = PublicFeeds.Excerpt(new string('x', 500), 100);
        Assert.Equal(100, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal("", PublicFeeds.Excerpt("  "));
    }

    [Fact]
    public void Tag_paths_are_url_safe()
    {
        Assert.Equal("/topics/C%23%20%26%20.NET", PublicFeeds.TagPath("C# & .NET"));
    }

    [Fact]
    public void The_sitemap_adds_the_extra_topic_pages_after_the_fixed_ones()
    {
        var urls = XDocument.Parse(PublicSite.Sitemap("https://example.test", new[] { "/topics/rag", "/topics/agents" }))
            .Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value).ToList();

        Assert.Contains("https://example.test/topics/rag", urls);
        Assert.Contains("https://example.test/topics", urls);
        Assert.Equal(urls.Count, urls.Distinct().Count());
    }

    [Fact]
    public void The_feed_is_allowed_for_crawlers_and_announced_in_the_page_head()
    {
        Assert.Contains("Allow: /feed.xml", PublicSite.Robots("https://example.test"));
        Assert.Contains("rel=\"alternate\" type=\"application/atom+xml\"", File.ReadAllText(Path.Combine(TestData.RepoRoot, "Components", "App.razor")));
    }

    [Fact]
    public void The_installable_app_files_exist_and_agree_with_each_other()
    {
        var wwwroot = Path.Combine(TestData.RepoRoot, "wwwroot");
        var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(wwwroot, "manifest.webmanifest"))).RootElement;

        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("name").GetString()));
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        var purposes = new List<string>();
        foreach (var icon in manifest.GetProperty("icons").EnumerateArray())
        {
            Assert.True(File.Exists(Path.Combine(wwwroot, icon.GetProperty("src").GetString()!)), "manifest icon missing: " + icon);
            purposes.Add(icon.GetProperty("purpose").GetString()!);
        }
        Assert.Contains("maskable", purposes);
        Assert.True(File.Exists(Path.Combine(wwwroot, "sw.js")));
        Assert.True(File.Exists(Path.Combine(wwwroot, "offline.html")));
        Assert.Contains("/offline.html", File.ReadAllText(Path.Combine(wwwroot, "sw.js")));
        var app = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Components", "App.razor"));
        Assert.Contains("rel=\"manifest\"", app);
        Assert.Contains("serviceWorker.register('sw.js')", app);
    }
}
