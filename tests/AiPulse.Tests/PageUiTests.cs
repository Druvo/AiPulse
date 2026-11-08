using AiPulse.Models;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests;

/// <summary>Presentation logic behind the Learn, Glossary, Tools, Ask, Settings and admin pages (Services/PageUi.cs).</summary>
public class PageUiTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(TestData.RepoRoot, relative));

    [Theory]
    [InlineData("RAG", "rag")]
    [InlineData("Retrieval-Augmented Generation (RAG)", "retrieval-augmented-generation-rag")]
    [InlineData("  Model Context Protocol  ", "model-context-protocol")]
    [InlineData("C#/.NET", "c-net")]
    [InlineData("***", "term")]
    [InlineData("", "term")]
    public void Glossary_slugs_are_fragment_safe(string term, string expected) =>
        Assert.Equal(expected, GlossaryIndex.Slug(term));

    [Fact]
    public void Every_real_glossary_term_gets_a_unique_deep_link()
    {
        var terms = TestData.Load<GlossaryTerm>("glossary.json");
        Assert.NotEmpty(terms);
        var slugs = terms.Select(t => GlossaryIndex.Slug(t.Term)).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.All(slugs, s => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$|^term$", s));
    }

    [Theory]
    [InlineData("agent", 'A')]
    [InlineData("Zero-shot", 'Z')]
    [InlineData("4-bit quantization", '#')]
    [InlineData("", '#')]
    [InlineData("  embedding", 'E')]
    public void Glossary_terms_file_under_their_first_letter(string term, char expected) =>
        Assert.Equal(expected, GlossaryIndex.LetterOf(term));

    [Fact]
    public void Glossary_alphabet_has_the_symbol_bucket_and_all_26_letters()
    {
        Assert.Equal(27, GlossaryIndex.Alphabet.Count);
        Assert.Equal('#', GlossaryIndex.Alphabet[0]);
        Assert.Equal('Z', GlossaryIndex.Alphabet[^1]);
    }

    [Fact]
    public void Glossary_search_matches_term_alias_and_definition_ignoring_case()
    {
        var t = new GlossaryTerm { Term = "Context window", Aliases = new[] { "headroom" }, Category = "Concept", ShortDef = "How many tokens a model can see." };
        Assert.True(GlossaryIndex.Matches(t, "CONTEXT"));
        Assert.True(GlossaryIndex.Matches(t, "headroom"));
        Assert.True(GlossaryIndex.Matches(t, "tokens"));
        Assert.True(GlossaryIndex.Matches(t, "   "));
        Assert.False(GlossaryIndex.Matches(t, "diffusion"));
    }

    [Fact]
    public void Copied_definition_includes_details_and_when_to_use_only_when_present()
    {
        var bare = new GlossaryTerm { Term = "RAG", Category = "Technique", ShortDef = "Look things up, then answer." };
        Assert.Equal("RAG: Look things up, then answer.", GlossaryIndex.CopyText(bare));

        var full = new GlossaryTerm { Term = "RAG", Category = "Technique", ShortDef = "Short.", Details = "Long.", WhenToUse = "Often." };
        Assert.Equal("RAG: Short.\n\nLong.\n\nWhen to use: Often.", GlossaryIndex.CopyText(full));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    [InlineData(5, 4, 100)]
    [InlineData(-1, 4, 0)]
    public void Progress_percent_is_rounded_and_clamped(int done, int total, int expected) =>
        Assert.Equal(expected, ProgressRing.Percent(done, total));

    [Fact]
    public void Progress_ring_offset_draws_the_right_share_of_the_circle()
    {
        var c = ProgressRing.Circumference(10);
        Assert.Equal(c, ProgressRing.DashOffset(10, 0), 6);
        Assert.Equal(0, ProgressRing.DashOffset(10, 100), 6);
        Assert.Equal(c / 4, ProgressRing.DashOffset(10, 75), 6);
        Assert.Equal(0, ProgressRing.DashOffset(10, 250), 6);
        Assert.Equal("62.832", ProgressRing.Svg(c));
    }

    [Fact]
    public void Uptime_strip_is_oldest_first_and_ends_today()
    {
        var today = new DateOnly(2026, 3, 10);
        var days = new Dictionary<string, bool> { ["2026-03-10"] = true, ["2026-03-09"] = false, ["2026-01-01"] = true };
        var strip = UptimeHistory.Strip(days, today, 5);
        Assert.Equal(new[] { UptimeDay.NoData, UptimeDay.NoData, UptimeDay.NoData, UptimeDay.Down, UptimeDay.Up }, strip);
        Assert.Equal(50, UptimeHistory.UptimePercent(strip));
        Assert.Null(UptimeHistory.UptimePercent(UptimeHistory.Strip(null, today, 5)));
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData(100.0, "ok")]
    [InlineData(90.0, "ok")]
    [InlineData(89.9, "warn")]
    [InlineData(60.0, "warn")]
    [InlineData(10.0, "bad")]
    public void Uptime_tone_uses_the_source_health_thresholds(double? pct, string tone) =>
        Assert.Equal(tone, UptimeHistory.Tone(pct));

    [Fact]
    public void Uptime_reader_is_fail_soft_and_reads_day_maps()
    {
        var root = Path.Combine(Path.GetTempPath(), "aipulse-uptime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(UptimeHistory.ReadAll(root)); // no App_Data at all

            var root2 = root + "-b";
            Directory.CreateDirectory(Path.Combine(root2, "App_Data"));
            File.WriteAllText(Path.Combine(root2, "App_Data", "source-health.json"),
                """{ "OpenAI": { "Days": { "2026-03-09": true, "2026-03-10": false }, "LastError": null }, "Odd": 5 }""");
            var all = UptimeHistory.ReadAll(root2);
            Assert.True(all["openai"]["2026-03-09"]);
            Assert.False(all["OpenAI"]["2026-03-10"]);
            Assert.False(all.ContainsKey("Odd"));
            Directory.Delete(root2, true);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tool_matrix_sorts_by_stars_with_unknown_counts_last_either_way()
    {
        var tools = new[]
        {
            new ToolEntry { Name = "B", Category = "X", OneLiner = "" },
            new ToolEntry { Name = "A", Category = "Y", OneLiner = "" },
            new ToolEntry { Name = "C", Category = "X", OneLiner = "" },
        };
        var stars = new Dictionary<string, int> { ["A"] = 10, ["C"] = 500 };

        Assert.Equal(new[] { "C", "A", "B" }, ToolMatrix.Sort(tools, "stars", false, stars).Select(t => t.Name));
        Assert.Equal(new[] { "A", "C", "B" }, ToolMatrix.Sort(tools, "stars", true, stars).Select(t => t.Name));
        Assert.Equal(new[] { "A", "B", "C" }, ToolMatrix.Sort(tools, "name", true, stars).Select(t => t.Name));
        Assert.Equal(new[] { "B", "C", "A" }, ToolMatrix.Sort(tools, "category", true, stars).Select(t => t.Name));
        Assert.Equal("12.3k", ToolMatrix.FormatStars(12_345));
        Assert.Equal("999", ToolMatrix.FormatStars(999));
    }

    [Fact]
    public void Settings_sections_hide_admin_from_regular_users_and_resolve_unknown_ids()
    {
        Assert.DoesNotContain(SettingsNav.For(false), s => s.Id == "admin");
        Assert.Contains(SettingsNav.For(true), s => s.Id == "admin");
        Assert.Equal("notifications", SettingsNav.Resolve("Notifications", false));
        Assert.Equal("profile", SettingsNav.Resolve("admin", false));
        Assert.Equal("admin", SettingsNav.Resolve("admin", true));
        Assert.Equal("profile", SettingsNav.Resolve(null, true));
    }

    [Fact]
    public void Every_settings_section_has_an_anchor_on_the_page()
    {
        var page = Read("Components/Pages/Settings.razor");
        foreach (var s in SettingsNav.Sections)
            Assert.Contains($"id=\"{s.Id}\"", page);
    }

    [Fact]
    public void Ask_answers_split_into_text_and_citations()
    {
        var parts = AskCitations.Segments("RAG retrieves first [1]. Then it answers [2][3].").ToList();
        Assert.Equal(new (string?, int?)[]
        {
            ("RAG retrieves first ", null), (null, 1), (". Then it answers ", null), (null, 2), (null, 3), (".", null)
        }, parts);
        Assert.Empty(AskCitations.Segments(""));
        Assert.Empty(AskCitations.Segments(null));
    }

    [Fact]
    public void Bulk_enable_copies_every_source_field_and_changes_only_enabled()
    {
        var s = new SourceRecord
        {
            Id = 7, Name = "N", Url = "https://x", Category = "News", Enabled = true, Note = "note", ContentType = "Paper", Level = "Advanced",
            TagsCsv = "a,b", FullTextFetch = true, IsScrape = true, ScrapeItemXPath = "//i", ScrapeTitleXPath = "t", ScrapeLinkXPath = "l", ScrapeDateXPath = "d"
        };
        var copy = AdminTable.WithEnabled(s, false);
        Assert.NotSame(s, copy);
        Assert.False(copy.Enabled);
        Assert.True(s.Enabled);
        foreach (var p in typeof(SourceRecord).GetProperties().Where(p => p.CanWrite && p.Name != nameof(SourceRecord.Enabled)))
            Assert.Equal(p.GetValue(s) is string[] arr ? string.Join(",", arr) : p.GetValue(s), p.GetValue(copy) is string[] arr2 ? string.Join(",", arr2) : p.GetValue(copy));
    }

    [Fact]
    public void Relative_times_are_compact()
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("never", AdminTable.Ago(null, now));
        Assert.Equal("just now", AdminTable.Ago(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", AdminTable.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", AdminTable.Ago(now.AddHours(-3), now));
        Assert.Equal("2 d ago", AdminTable.Ago(now.AddDays(-2), now));
        Assert.Equal("just now", AdminTable.Ago(now.AddMinutes(5), now));
        Assert.Equal("A", AdminTable.Initial(" alice"));
        Assert.Equal("?", AdminTable.Initial(""));
    }
}
