using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Productivity;
using Xunit;

namespace AiPulse.Tests;

public class SavedSearchQueryTests
{
    [Fact]
    public void Plain_words_must_all_appear_case_insensitively()
    {
        var q = SavedSearchQuery.Parse("agent memory");
        Assert.True(q.Matches(Items.Make("New Agent framework adds long-term MEMORY")));
        Assert.False(q.Matches(Items.Make("New agent framework")));
    }

    [Fact]
    public void Quoted_phrases_match_verbatim_and_exclusions_reject()
    {
        var q = SavedSearchQuery.Parse("\"open weights\" -llama");
        Assert.Equal(new[] { "open weights" }, q.Terms);
        Assert.Equal(new[] { "llama" }, q.Excluded);
        Assert.True(q.Matches(Items.Make("Mistral ships open weights model")));
        Assert.False(q.Matches(Items.Make("Weights are open now")));
        Assert.False(q.Matches(Items.Make("Llama 5 with open weights")));
    }

    [Fact]
    public void Excluded_quoted_phrase_is_supported()
    {
        var q = SavedSearchQuery.Parse("model -\"press release\"");
        Assert.Equal(new[] { "press release" }, q.Excluded);
        Assert.False(q.Matches(Items.Make("Model news", summary: "Read the press release")));
    }

    [Fact]
    public void Summary_and_tags_are_searched_too()
    {
        var q = SavedSearchQuery.Parse("retrieval");
        Assert.True(q.Matches(Items.Make("Some title", summary: "<p>A retrieval trick</p>")));
        Assert.True(q.Matches(Items.Make("Other", tags: new[] { "Retrieval" })));
    }

    [Fact]
    public void Tags_match_any_listed_tag_exactly()
    {
        var q = SavedSearchQuery.Parse("", "MCP, agents");
        Assert.True(q.Matches(Items.Make("x", tags: new[] { "mcp" })));
        Assert.True(q.Matches(Items.Make("y", tags: new[] { " Agents " })));
        Assert.False(q.Matches(Items.Make("z", tags: new[] { "MCP-server" })));
    }

    [Fact]
    public void Sources_match_any_listed_source_and_names_may_contain_commas()
    {
        var q = SavedSearchQuery.Parse("", null, "Simon Willison, blog\nHugging Face");
        Assert.True(q.Matches(Items.Make("a", source: "simon willison, blog")));
        Assert.True(q.Matches(Items.Make("b", source: "Hugging Face")));
        Assert.False(q.Matches(Items.Make("c", source: "OpenAI")));
    }

    [Fact]
    public void Inline_tag_and_source_prefixes_are_understood()
    {
        var q = SavedSearchQuery.Parse("tag:RAG source:\"Hugging Face\" eval");
        Assert.Equal(new[] { "RAG" }, q.Tags);
        Assert.Equal(new[] { "Hugging Face" }, q.Sources);
        Assert.Equal(new[] { "eval" }, q.Terms);
    }

    [Fact]
    public void All_criteria_must_hold_together()
    {
        var q = SavedSearchQuery.Parse("benchmark", "LLM", "Lab");
        Assert.True(q.Matches(Items.Make("New benchmark", source: "Lab", tags: new[] { "LLM" })));
        Assert.False(q.Matches(Items.Make("New benchmark", source: "Other", tags: new[] { "LLM" })));
        Assert.False(q.Matches(Items.Make("New benchmark", source: "Lab", tags: new[] { "Vision" })));
    }

    [Fact]
    public void Empty_query_matches_nothing_and_exclusion_only_has_no_positive_criterion()
    {
        Assert.False(SavedSearchQuery.Parse("  ").Matches(Items.Make("anything")));
        var q = SavedSearchQuery.Parse("-crypto");
        Assert.False(q.HasPositiveCriterion);
    }

    [Fact]
    public void Unbalanced_quote_runs_to_the_end_and_duplicates_collapse()
    {
        var q = SavedSearchQuery.Parse("Agent agent \"open source");
        Assert.Equal(new[] { "Agent", "open source" }, q.Terms);
    }

    [Fact]
    public void A_lone_dash_is_a_word_not_an_exclusion()
    {
        var q = SavedSearchQuery.Parse("a - b");
        Assert.Contains("-", q.Terms);
        Assert.Empty(q.Excluded);
    }

    [Fact]
    public void Count_honours_the_since_bound()
    {
        var q = SavedSearchQuery.Parse("gpu");
        var items = new[]
        {
            Items.Make("GPU one", published: Items.Now.AddDays(-1)),
            Items.Make("GPU two", published: Items.Now.AddDays(-10)),
            Items.Make("CPU", published: Items.Now)
        };
        Assert.Equal(2, q.Count(items));
        Assert.Equal(1, q.Count(items, Items.Now.AddDays(-2)));
    }

    [Fact]
    public void Describe_is_readable()
    {
        Assert.Equal("agents \"tool use\" · -crypto · tags: MCP · source: Lab", SavedSearchQuery.Parse("agents \"tool use\" -crypto", "MCP", "Lab").Describe());
    }

    [Fact]
    public void Link_hash_is_stable_and_short()
    {
        var a = SavedSearchQuery.LinkHash("https://x.test/a");
        Assert.Equal(a, SavedSearchQuery.LinkHash("https://x.test/a"));
        Assert.NotEqual(a, SavedSearchQuery.LinkHash("https://x.test/b"));
        Assert.Equal(40, a.Length);
    }
}

public class SavedSearchAlertingTests
{
    private static readonly SavedSearchAlertOptions Opts = new();
    private static readonly DateTime Now = Items.Now.UtcDateTime;

    private static SavedSearchRule Rule(int id, int user, string keywords, bool alert = true, DateTime? lastAlert = null, DateTime? since = null) => new()
    {
        Id = id, UserId = user, Name = "r" + id, Keywords = keywords, AlertEnabled = alert, LastAlertUtc = lastAlert, AlertsSinceUtc = since
    };

    private static ISet<string> None(int _) => new HashSet<string>();

    [Fact]
    public void A_rule_with_new_matches_gets_one_plan_grouping_them_newest_first()
    {
        var items = new[]
        {
            Items.Make("agent one", published: Items.Now.AddHours(-3)),
            Items.Make("agent two", published: Items.Now.AddHours(-1)),
            Items.Make("unrelated")
        };
        var plan = Assert.Single(SavedSearchAlerting.Plan(new[] { Rule(1, 7, "agent") }, items, None, Now, Opts));
        Assert.Equal(new[] { "agent two", "agent one" }, plan.NewMatches.Select(i => i.Title));
    }

    [Fact]
    public void Already_handled_links_never_alert_again()
    {
        var item = Items.Make("agent news");
        var handled = new HashSet<string> { SavedSearchQuery.LinkHash(item.Link) };
        Assert.Empty(SavedSearchAlerting.Plan(new[] { Rule(1, 7, "agent") }, new[] { item }, _ => handled, Now, Opts));
    }

    [Fact]
    public void Disabled_rules_and_exclusion_only_rules_are_skipped()
    {
        var items = new[] { Items.Make("agent news") };
        Assert.Empty(SavedSearchAlerting.Plan(new[] { Rule(1, 7, "agent", alert: false), Rule(2, 7, "-crypto") }, items, None, Now, Opts));
    }

    [Fact]
    public void Items_older_than_the_window_do_not_alert()
    {
        var items = new[] { Items.Make("agent old", published: Items.Now.AddDays(-8)) };
        Assert.Empty(SavedSearchAlerting.Plan(new[] { Rule(1, 7, "agent") }, items, None, Now, Opts));
    }

    [Fact]
    public void Items_published_well_before_alerts_were_enabled_do_not_alert()
    {
        var items = new[]
        {
            Items.Make("agent before", published: Items.Now.AddDays(-4)),
            Items.Make("agent after", published: Items.Now.AddHours(-1))
        };
        var plan = Assert.Single(SavedSearchAlerting.Plan(new[] { Rule(1, 7, "agent", since: Now.AddDays(-1)) }, items, None, Now, Opts));
        Assert.Equal("agent after", Assert.Single(plan.NewMatches).Title);
    }

    [Fact]
    public void Per_user_cap_limits_alerts_and_defers_the_rest()
    {
        var opts = new SavedSearchAlertOptions { MaxAlertsPerUserPerPoll = 2 };
        var rules = Enumerable.Range(1, 4).Select(i => Rule(i, 7, "agent")).Append(Rule(9, 8, "agent")).ToList();
        var plans = SavedSearchAlerting.Plan(rules, new[] { Items.Make("agent news") }, None, Now, opts);
        Assert.Equal(2, plans.Count(p => p.Rule.UserId == 7));
        Assert.Single(plans, p => p.Rule.UserId == 8);
    }

    [Fact]
    public void Global_cap_stops_the_pass()
    {
        var opts = new SavedSearchAlertOptions { MaxAlertsPerPoll = 3 };
        var rules = Enumerable.Range(1, 10).Select(i => Rule(i, i, "agent")).ToList();
        Assert.Equal(3, SavedSearchAlerting.Plan(rules, new[] { Items.Make("agent news") }, None, Now, opts).Count);
    }

    [Fact]
    public void Least_recently_alerted_rules_go_first_so_caps_rotate_fairly()
    {
        var opts = new SavedSearchAlertOptions { MaxAlertsPerPoll = 1 };
        var rules = new[] { Rule(1, 1, "agent", lastAlert: Now.AddMinutes(-5)), Rule(2, 2, "agent", lastAlert: Now.AddDays(-1)), Rule(3, 3, "agent", lastAlert: Now.AddHours(-1)) };
        Assert.Equal(2, Assert.Single(SavedSearchAlerting.Plan(rules, new[] { Items.Make("agent news") }, None, Now, opts)).Rule.Id);
    }

    [Fact]
    public void Matches_per_rule_are_capped_per_pass()
    {
        var opts = new SavedSearchAlertOptions { MaxMatchesPerRulePerPoll = 3 };
        var items = Enumerable.Range(1, 10).Select(i => Items.Make("agent " + i, published: Items.Now.AddMinutes(-i))).ToList();
        var plan = Assert.Single(SavedSearchAlerting.Plan(new[] { Rule(1, 1, "agent") }, items, None, Now, opts));
        Assert.Equal(new[] { "agent 1", "agent 2", "agent 3" }, plan.NewMatches.Select(i => i.Title));
    }

    [Fact]
    public void Duplicate_links_in_the_input_count_once()
    {
        var a = Items.Make("agent a", link: "https://x.test/1");
        var plan = Assert.Single(SavedSearchAlerting.Plan(new[] { Rule(1, 1, "agent") }, new[] { a, a with { Title = "agent a (updated)" } }, None, Now, Opts));
        Assert.Single(plan.NewMatches);
    }

    [Fact]
    public void One_match_links_to_the_item_and_several_link_to_the_saved_page()
    {
        var rule = Rule(5, 1, "agent");
        var one = SavedSearchAlerting.ToAlert(new RuleAlertPlan(rule, new[] { Items.Make("agent a", source: "Lab") }), "alice", Items.Now);
        Assert.Equal("https://example.test/agent-a", one.Link);
        Assert.Equal("alice", one.Audience);
        Assert.Equal("Lab", one.SourceName);
        var many = SavedSearchAlerting.ToAlert(new RuleAlertPlan(rule, new[] { Items.Make("agent a"), Items.Make("agent b") }), "alice", Items.Now);
        Assert.Equal("/saved#rule-5", many.Link);
        Assert.Equal(2, many.Count);
        Assert.Equal("agent a", many.Details);
    }
}

public class SavedSearchServiceTests
{
    private static (SavedSearchService Service, TestProductivityDb Db, FakeClock Clock) Make()
    {
        var db = new TestProductivityDb();
        var clock = new FakeClock(Items.Now);
        return (new SavedSearchService(db.Db, clock), db, clock);
    }

    private static readonly Dictionary<int, string> Users = new() { [1] = "alice", [2] = "bob" };

    [Fact]
    public async Task Validation_rejects_missing_name_and_empty_or_exclusion_only_queries()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        Assert.NotNull((await svc.SaveAsync(1, new SavedSearchRule { Name = "", Keywords = "x" }, Array.Empty<FeedItem>())).Error);
        Assert.NotNull((await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Keywords = "" }, Array.Empty<FeedItem>())).Error);
        Assert.NotNull((await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Keywords = "-crypto" }, Array.Empty<FeedItem>())).Error);
        Assert.NotNull((await svc.SaveAsync(1, new SavedSearchRule { Name = new string('n', 81), Keywords = "a" }, Array.Empty<FeedItem>())).Error);
        Assert.Empty(await svc.ListAsync(1));
    }

    [Fact]
    public async Task Rules_are_private_to_their_owner()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "Agents", Keywords = "agent" }, Array.Empty<FeedItem>());
        Assert.Single(await svc.ListAsync(1));
        Assert.Empty(await svc.ListAsync(2));
        Assert.False(await svc.DeleteAsync(2, rule!.Id));
        var (_, error) = await svc.SaveAsync(2, new SavedSearchRule { Id = rule.Id, Name = "Hijack", Keywords = "x" }, Array.Empty<FeedItem>());
        Assert.NotNull(error);
        Assert.Equal("Agents", (await svc.ListAsync(1))[0].Name);
    }

    [Fact]
    public async Task Tags_and_sources_are_normalised_on_save()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Tags = " MCP,mcp,, agents ", Sources = "A\r\n\nB\nA" }, Array.Empty<FeedItem>());
        Assert.Equal("MCP, agents", rule!.Tags);
        Assert.Equal("A\nB", rule.Sources);
    }

    [Fact]
    public async Task Enabling_alerts_records_the_backlog_so_only_new_items_alert_and_each_only_once()
    {
        var (svc, db, clock) = Make();
        using var _d = db;
        var backlog = new List<FeedItem> { Items.Make("agent old news", published: Items.Now.AddHours(-2)) };
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "Agents", Keywords = "agent", AlertEnabled = true }, backlog);
        Assert.NotNull(rule!.AlertsSinceUtc);

        Assert.Empty(await svc.EvaluateAsync(backlog, Users, new SavedSearchAlertOptions()));

        clock.Advance(TimeSpan.FromMinutes(15));
        var fresh = Items.Make("agent fresh news", published: clock.Now.AddMinutes(-5));
        backlog.Add(fresh);
        var result = Assert.Single(await svc.EvaluateAsync(backlog, Users, new SavedSearchAlertOptions()));
        Assert.Equal("alice", result.Username);
        Assert.Equal(fresh.Link, Assert.Single(result.Plan.NewMatches).Link);

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Empty(await svc.EvaluateAsync(backlog, Users, new SavedSearchAlertOptions()));

        var recent = await svc.RecentAlertsAsync(1);
        Assert.Equal(fresh.Link, Assert.Single(recent).Link);
        Assert.NotNull((await svc.ListAsync(1))[0].LastAlertUtc);
    }

    [Fact]
    public async Task Rules_of_users_who_are_not_approved_are_skipped()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        await svc.SaveAsync(3, new SavedSearchRule { Name = "x", Keywords = "agent", AlertEnabled = true }, Array.Empty<FeedItem>());
        Assert.Empty(await svc.EvaluateAsync(new[] { Items.Make("agent!") }, Users, new SavedSearchAlertOptions()));
    }

    [Fact]
    public async Task Turning_alerts_off_and_on_again_takes_a_new_baseline()
    {
        var (svc, db, clock) = Make();
        using var _d = db;
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Keywords = "agent" }, Array.Empty<FeedItem>());
        var items = new[] { Items.Make("agent a") };
        Assert.Empty(await svc.EvaluateAsync(items, Users, new SavedSearchAlertOptions()));
        Assert.True(await svc.SetAlertAsync(1, rule!.Id, true, items));
        Assert.Empty(await svc.EvaluateAsync(items, Users, new SavedSearchAlertOptions()));
        Assert.False(await svc.SetAlertAsync(2, rule.Id, false, items));
    }

    [Fact]
    public async Task Editing_the_query_rebaselines_instead_of_alerting_on_old_matches()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        var items = new[] { Items.Make("agent a"), Items.Make("model b") };
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Keywords = "agent", AlertEnabled = true }, items);
        rule!.Keywords = "model";
        await svc.SaveAsync(1, rule, items);
        Assert.Empty(await svc.EvaluateAsync(items, Users, new SavedSearchAlertOptions()));
    }

    [Fact]
    public async Task Deleting_a_rule_removes_its_history()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        var (rule, _) = await svc.SaveAsync(1, new SavedSearchRule { Name = "n", Keywords = "agent", AlertEnabled = true }, Array.Empty<FeedItem>());
        await svc.EvaluateAsync(new[] { Items.Make("agent a") }, Users, new SavedSearchAlertOptions());
        Assert.Single(await svc.RecentAlertsAsync(1));
        Assert.True(await svc.DeleteAsync(1, rule!.Id));
        Assert.Empty(await svc.RecentAlertsAsync(1));
    }

    [Fact]
    public async Task There_is_a_limit_on_rules_per_user()
    {
        var (svc, db, _) = Make();
        using var _d = db;
        for (var i = 0; i < SavedSearchService.MaxRulesPerUser; i++)
            Assert.Null((await svc.SaveAsync(1, new SavedSearchRule { Name = "n" + i, Keywords = "k" }, Array.Empty<FeedItem>())).Error);
        Assert.NotNull((await svc.SaveAsync(1, new SavedSearchRule { Name = "one more", Keywords = "k" }, Array.Empty<FeedItem>())).Error);
        Assert.Equal(SavedSearchService.MaxRulesPerUser, await svc.CountAsync(1));
    }
}

public class NotificationAudienceTests
{
    [Fact]
    public void Targeted_alerts_stay_out_of_the_shared_list_and_event()
    {
        var n = new NotificationService();
        var shared = new List<Alert>();
        var personal = new List<(string, int)>();
        n.OnChange += a => shared.AddRange(a);
        n.OnUserAlerts += (user, a) => personal.Add((user, a.Count));

        n.Add(new[]
        {
            new Alert { Title = "release", Link = "/x", Kind = "Release" },
            new Alert { Title = "mine", Link = "/y", Kind = "Search", Audience = "alice" },
        });

        Assert.Equal("release", Assert.Single(shared).Title);
        Assert.Equal(("alice", 1), Assert.Single(personal));
        Assert.Single(n.Recent);
        Assert.Equal(1, n.UnreadCount);
        Assert.Equal(2, n.RecentFor("ALICE").Count);
        Assert.Single(n.RecentFor("bob"));
        Assert.Equal(2, n.UnreadCountFor("alice"));
        Assert.Equal(1, n.UnreadCountFor(null));
    }

    [Fact]
    public void Mark_all_read_for_a_user_does_not_touch_other_users_alerts()
    {
        var n = new NotificationService();
        n.Add(new[]
        {
            new Alert { Title = "a", Link = "/a", Kind = "Search", Audience = "alice" },
            new Alert { Title = "b", Link = "/b", Kind = "Search", Audience = "bob" },
        });
        n.MarkAllReadFor("alice");
        Assert.Equal(0, n.UnreadCountFor("alice"));
        Assert.Equal(1, n.UnreadCountFor("bob"));
        n.MarkAllRead();
        Assert.Equal(1, n.UnreadCountFor("bob"));
    }
}
