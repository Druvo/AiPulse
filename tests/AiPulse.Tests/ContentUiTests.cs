using AiPulse.Models;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests;

public class ContentUiTests
{
    // 14:30 local in a +06:00 zone, so local-day logic is exercised away from UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 30, 0, TimeSpan.FromHours(6));

    private static FeedItem Item(string title, double hoursAgo = 1, string[]? tags = null, string type = "News") => new()
    {
        Title = title,
        Link = "https://example.test/" + Uri.EscapeDataString(title),
        Published = Now.AddHours(-hoursAgo),
        SourceName = "Example",
        Category = "News",
        ContentType = type,
        Tags = tags ?? Array.Empty<string>()
    };

    [Theory]
    [InlineData("comfortable", FeedDensity.Comfortable)]
    [InlineData("List", FeedDensity.Comfortable)]
    [InlineData("COMPACT", FeedDensity.Compact)]
    [InlineData(" grid ", FeedDensity.Grid)]
    [InlineData(null, FeedDensity.Grid)]
    [InlineData("", FeedDensity.Grid)]
    [InlineData("cards-v9", FeedDensity.Grid)]
    public void Density_parses_current_and_legacy_names_and_falls_back_on_anything_else(string? value, FeedDensity expected) =>
        Assert.Equal(expected, ContentUi.ParseDensity(value));

    [Fact]
    public void Density_round_trips_through_its_storage_key_and_maps_to_a_card_layout()
    {
        foreach (var d in Enum.GetValues<FeedDensity>())
            Assert.Equal(d, ContentUi.ParseDensity(ContentUi.DensityKey(d), fallback: d == FeedDensity.Grid ? FeedDensity.Compact : FeedDensity.Grid));

        Assert.Equal("List", ContentUi.CardLayout(FeedDensity.Comfortable));
        Assert.Equal("Compact", ContentUi.CardLayout(FeedDensity.Compact));
        Assert.Equal("Grid", ContentUi.CardLayout(FeedDensity.Grid));
    }

    [Fact]
    public void Relative_time_reads_naturally_at_every_age()
    {
        Assert.Equal("just now", ContentUi.RelativeTime(Now.AddSeconds(-20), Now));
        Assert.Equal("just now", ContentUi.RelativeTime(Now.AddMinutes(2), Now)); // a feed clock slightly ahead of ours
        Assert.Equal("5m ago", ContentUi.RelativeTime(Now.AddMinutes(-5), Now));
        Assert.Equal("3h ago", ContentUi.RelativeTime(Now.AddHours(-3), Now));
        Assert.Equal("Yesterday", ContentUi.RelativeTime(Now.AddHours(-20), Now));
        Assert.Equal("Mon", ContentUi.RelativeTime(Now.AddDays(-3), Now)); // Nov 5 2026 is a Thursday
        Assert.Equal("Oct 20", ContentUi.RelativeTime(Now.AddDays(-16), Now));
        Assert.Equal("Nov 5, 2025", ContentUi.RelativeTime(Now.AddYears(-1), Now));
    }

    [Fact]
    public void Relative_time_uses_the_viewers_day_not_utcs()
    {
        // 00:30 local on Nov 5 is still Nov 4 in UTC; 2 hours later it must read as "today", not "Yesterday".
        var now = new DateTimeOffset(2026, 11, 5, 2, 30, 0, TimeSpan.FromHours(6));
        Assert.Equal("2h ago", ContentUi.RelativeTime(new DateTimeOffset(2026, 11, 4, 18, 30, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void Highlight_marks_every_term_case_insensitively_and_reassembles_exactly()
    {
        var segs = ContentUi.Highlight("Retrieval-Augmented Generation for RAG apps", "rag GEN");

        Assert.Equal("Retrieval-Augmented Generation for RAG apps", string.Concat(segs.Select(s => s.Text)));
        Assert.Equal(new[] { "Gen", "RAG" }, segs.Where(s => s.Match).Select(s => s.Text));
    }

    [Fact]
    public void Highlight_merges_overlapping_matches_and_handles_empty_input()
    {
        var segs = ContentUi.Highlight("transformers", "trans sformers");
        Assert.Single(segs);
        Assert.True(segs[0].Match);

        Assert.Empty(ContentUi.Highlight("", "x"));
        Assert.Equal(new[] { new TextSegment("abc", false) }, ContentUi.Highlight("abc", "   "));
        Assert.Equal(new[] { new TextSegment("abc", false) }, ContentUi.Highlight("abc", "zzz"));
    }

    [Fact]
    public void Match_score_ranks_exact_then_prefix_then_word_start_then_anywhere_then_subtitle()
    {
        var exact = ContentUi.MatchScore("RAG", "", "rag");
        var prefix = ContentUi.MatchScore("RAG pipelines", "", "rag");
        var word = ContentUi.MatchScore("Graph RAG", "", "rag");
        var inside = ContentUi.MatchScore("Storage", "", "rag");
        var subtitle = ContentUi.MatchScore("Vector stores", "Used for RAG", "rag");

        Assert.True(exact > prefix && prefix > word && word > inside && inside > subtitle && subtitle > 0);
        Assert.Equal(0, ContentUi.MatchScore("Vector stores", "embeddings", "rag"));
        Assert.Equal(0, ContentUi.MatchScore("RAG", "", "rag missingterm")); // every term must match somewhere
        Assert.Equal(0, ContentUi.MatchScore("RAG", "", " "));
    }

    [Fact]
    public void Streak_counts_back_from_today_and_survives_an_empty_morning()
    {
        var today = new DateOnly(2026, 11, 5);
        Assert.Equal(3, ContentUi.ReadingStreak(new[] { today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-4) }, today));
        Assert.Equal(2, ContentUi.ReadingStreak(new[] { today.AddDays(-1), today.AddDays(-2) }, today)); // nothing read yet today
        Assert.Equal(0, ContentUi.ReadingStreak(new[] { today.AddDays(-2) }, today));
        Assert.Equal(0, ContentUi.ReadingStreak(Array.Empty<DateOnly>(), today));
    }

    [Fact]
    public void Dashboard_summary_counts_todays_headlines_unread_this_week_and_the_top_topic()
    {
        var items = new[]
        {
            Item("a", hoursAgo: 1, tags: new[] { "agents" }),
            Item("b", hoursAgo: 10, tags: new[] { "Agents", "rag" }),
            Item("c", hoursAgo: 20, tags: new[] { "rag" }),      // yesterday local (14:30 - 20h)
            Item("d", hoursAgo: 24 * 3, tags: new[] { "agents" }),
            Item("e", hoursAgo: 24 * 10, tags: new[] { "rag" }), // older than a week
            Item("r", hoursAgo: 2, type: "Release")              // releases aren't headlines
        };
        var read = new HashSet<string> { items[0].Link };
        var readTimes = new[] { Now.AddHours(-1), Now.AddDays(-1) };

        var s = ContentUi.Summarize(items, read.Contains, readTimes, Now);

        Assert.Equal(2, s.NewToday);
        Assert.Equal(3, s.UnreadThisWeek);
        Assert.Equal(2, s.Streak);
        Assert.Equal("agents", s.TopTopic);
        Assert.Equal(3, s.TopTopicCount);
    }

    [Fact]
    public void Dashboard_summary_is_all_zero_on_a_fresh_install()
    {
        var s = ContentUi.Summarize(Array.Empty<FeedItem>(), _ => false, Array.Empty<DateTimeOffset>(), Now);
        Assert.Equal(new DashboardSummary(0, 0, 0, null, 0), s);
    }

    [Fact]
    public void Heat_level_keeps_quiet_days_visible_next_to_an_outlier()
    {
        Assert.Equal(0, ContentUi.HeatLevel(0, 500));
        Assert.Equal(0, ContentUi.HeatLevel(3, 0));
        Assert.Equal(1, ContentUi.HeatLevel(1, 500));
        Assert.Equal(2, ContentUi.HeatLevel(60, 500));  // a straight ratio would put 12% of max at level 1
        Assert.Equal(4, ContentUi.HeatLevel(500, 500));
        Assert.Equal(4, ContentUi.HeatLevel(900, 500)); // clamped
        var levels = Enumerable.Range(0, 101).Select(c => ContentUi.HeatLevel(c, 100)).ToList();
        Assert.Equal(levels.OrderBy(x => x), levels); // monotonic
    }

    [Fact]
    public void Topic_tier_spans_one_to_five_and_is_monotonic()
    {
        Assert.Equal(1, ContentUi.TopicTier(2, 1));
        Assert.Equal(1, ContentUi.TopicTier(1, 300));
        Assert.Equal(5, ContentUi.TopicTier(300, 300));
        var tiers = Enumerable.Range(1, 300).Select(c => ContentUi.TopicTier(c, 300)).ToList();
        Assert.Equal(tiers.OrderBy(x => x), tiers);
        Assert.Equal(5, tiers.Distinct().Count());
    }

    [Fact]
    public void Group_by_day_labels_today_yesterday_weekdays_then_dates()
    {
        var items = new[] { Item("a", 1), Item("b", 2), Item("c", 20), Item("d", 24 * 3), Item("e", 24 * 30), Item("f", 24 * 400) };

        var groups = ContentUi.GroupByDay(items, Now);

        Assert.Equal(new[] { "Today", "Yesterday", "Monday", "Tue, Oct 6", "Oct 1, 2025" }, groups.Select(g => g.Label));
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Equal(new[] { "a", "b" }, groups[0].Items.Select(i => i.Title)); // order within a day is preserved
    }

    [Fact]
    public void Daily_counts_bucket_by_local_day_oldest_first_and_ignore_out_of_range()
    {
        var times = new[] { Now.AddHours(-1), Now.AddHours(-2), Now.AddHours(-20), Now.AddDays(-6), Now.AddDays(-30), Now.AddDays(1) };
        var counts = ContentUi.DailyCounts(times, Now, 7);
        Assert.Equal(new[] { 1, 0, 0, 0, 0, 1, 2 }, counts); // -6d, ..., yesterday, today
        Assert.Empty(ContentUi.DailyCounts(times, Now, 0));
    }

    [Fact]
    public void Spark_path_spans_the_box_with_the_peak_at_the_top()
    {
        var path = ContentUi.SparkPath(new[] { 0, 5, 10 }, 100, 20);
        Assert.Equal("M0,18.5 L50,10 L100,1.5", path);
        Assert.Equal("", ContentUi.SparkPath(Array.Empty<int>(), 100, 20));
        Assert.Equal("M0,18.5 L100,18.5", ContentUi.SparkPath(new[] { 0, 0 }, 100, 20)); // all-zero stays a flat baseline
    }

    [Fact]
    public void Related_tags_rank_co_occurring_tags_and_exclude_the_topic_itself()
    {
        var items = new[]
        {
            Item("a", tags: new[] { "RAG", "agents", "LLM" }),
            Item("b", tags: new[] { "rag", "agents" }),
            Item("c", tags: new[] { "Rag", "vector-db" }),
            Item("d", tags: new[] { "agents", "LLM" }) // no RAG: doesn't count
        };

        var related = ContentUi.RelatedTags(items, "rag");

        Assert.Equal(new[] { "agents", "LLM", "vector-db" }, related.Select(t => t.Tag));
        Assert.Equal(new[] { 2, 1, 1 }, related.Select(t => t.Count));
    }

    [Theory]
    [InlineData("huggingface/transformers v4.46.0", "huggingface/transformers", "v4.46.0")]
    [InlineData("ollama 0.5.1-rc2", "ollama", "0.5.1-rc2")]
    [InlineData("v1.2.3", "v1.2.3", "v1.2.3")]
    [InlineData("LangChain: release notes", "LangChain: release notes", null)]
    [InlineData("llama.cpp b4061", "llama.cpp b4061", null)]
    public void Split_release_separates_the_project_from_its_version(string title, string project, string? version)
    {
        var (p, v) = ContentUi.SplitRelease(title);
        Assert.Equal(project, p);
        Assert.Equal(version, v);
    }
}
