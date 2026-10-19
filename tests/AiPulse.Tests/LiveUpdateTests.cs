using System.Security.Claims;
using AiPulse.Models;
using AiPulse.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace AiPulse.Tests;

public class SiteStatsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    private static FeedItem Item(string link, TimeSpan age, string type = "News") =>
        new() { Title = link, Link = link, Published = Now - age, SourceName = "s", Category = "News", ContentType = type };

    [Fact]
    public void Counts_only_headlines_from_the_last_24_hours()
    {
        var items = new[] { Item("a", TimeSpan.FromHours(1)), Item("b", TimeSpan.FromHours(23)), Item("c", TimeSpan.FromHours(25)), Item("d", TimeSpan.FromDays(5)) };

        var stats = SiteStatsService.Compute(80, 36, 17, items, Now, null, 15);

        Assert.Equal(2, stats.ItemsLast24Hours);
        Assert.Equal(Now - TimeSpan.FromHours(1), stats.NewestItem);
    }

    [Fact]
    public void Release_items_and_items_dated_in_the_future_are_not_headlines()
    {
        var items = new[] { Item("rel", TimeSpan.FromHours(1), "Release"), Item("future", TimeSpan.FromHours(-5)), Item("real", TimeSpan.FromHours(2)) };

        var stats = SiteStatsService.Compute(1, 1, 1, items, Now, null, 15);

        Assert.Equal(1, stats.ItemsLast24Hours);
        Assert.Equal(Now - TimeSpan.FromHours(2), stats.NewestItem);
    }

    [Fact]
    public void The_counts_passed_in_come_straight_back_and_an_empty_feed_has_no_newest_item()
    {
        var stats = SiteStatsService.Compute(79, 36, 17, Array.Empty<FeedItem>(), Now, Now.AddMinutes(-3), 15);

        Assert.Equal((79, 36, 17, 0, 15), (stats.ActiveSources, stats.GlossaryTerms, stats.LearningModules, stats.ItemsLast24Hours, stats.PollMinutes));
        Assert.Null(stats.NewestItem);
        Assert.Equal(Now.AddMinutes(-3), stats.LastPolled);
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Ago_reads_like_a_person_would_say_it(int seconds, string expected) =>
        Assert.Equal(expected, SiteStatsService.Ago(Now - TimeSpan.FromSeconds(seconds), Now));
}

public class FeedHistoryChangedTests
{
    private static FeedItem Item(string link) =>
        new() { Title = link, Link = link, Published = DateTimeOffset.Now, SourceName = "s", Category = "News" };

    [Fact]
    public void Recording_new_items_raises_Changed_once_and_recording_them_again_does_not()
    {
        using var env = new TempEnvironment();
        var history = new FeedHistoryService(env);
        var raised = 0;
        history.Changed += () => raised++;

        history.Record(new[] { Item("https://e.com/1"), Item("https://e.com/2") });
        history.Record(new[] { Item("https://e.com/1"), Item("https://e.com/2") });

        Assert.Equal(1, raised);
        Assert.Equal(2, history.Items.Count);
    }

    [Fact]
    public void By_the_time_Changed_fires_the_new_items_are_already_readable()
    {
        using var env = new TempEnvironment();
        var history = new FeedHistoryService(env);
        var seenAtEvent = -1;
        history.Changed += () => seenAtEvent = history.Items.Count;

        history.Record(new[] { Item("https://e.com/1") });

        Assert.Equal(1, seenAtEvent);
    }
}

public class ReadChangedTests
{
    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static FeedItem Item(string link) =>
        new() { Title = link, Link = link, Published = DateTimeOffset.Now, SourceName = "s", Category = "News" };

    private static (ReadingStateService Service, Func<int> Raised, TempEnvironment Env) Make()
    {
        var env = new TempEnvironment();
        var service = new ReadingStateService(env, new Anonymous());
        var raised = 0;
        service.ReadChanged += () => raised++;
        return (service, () => raised, env);
    }

    [Fact]
    public void Marking_read_and_unread_raises_the_event_only_when_something_changed()
    {
        var (service, raised, env) = Make();
        using var _ = env;

        service.MarkRead("a", true);
        service.MarkRead("a", true);   // already read: no change
        service.MarkRead("a", false);
        service.MarkRead("a", false);  // already unread: no change

        Assert.Equal(2, raised());
    }

    [Fact]
    public void Toggling_opening_and_bulk_changes_all_raise_it()
    {
        var (service, raised, env) = Make();
        using var _ = env;

        service.ToggleRead(Item("a"));                                   // 1
        service.EnsureRead(Item("b"));                                   // 2
        service.EnsureRead(Item("b"));                                   // already read: none
        var marked = service.EnsureReadBulk(new[] { Item("c"), Item("d"), Item("a") }); // 3 (one event for the batch)
        service.MarkUnreadBulk(new[] { "c", "d" });                      // 4
        service.MarkUnreadBulk(new[] { "never-read" });                  // none

        Assert.Equal(2, marked);
        Assert.Equal(4, raised());
    }

    [Fact]
    public void A_handler_can_read_the_new_state_because_the_event_fires_outside_the_lock()
    {
        var env = new TempEnvironment();
        using var _ = env;
        var service = new ReadingStateService(env, new Anonymous());
        var readInsideHandler = false;
        service.ReadChanged += () => readInsideHandler = service.IsRead("a");

        service.MarkRead("a", true);

        Assert.True(readInsideHandler);
    }
}

public class FeedCacheUpdatedTests
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>A real aggregator with no sources configured, so a poll completes instantly with no network or database.</summary>
    private static FeedAggregatorService Aggregator(TempEnvironment env) => new(
        new Factory(),
        new KnowledgeBaseService(env, null!),
        new SourceHealthService(env),
        new ContentExtractorService(new Factory(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ContentExtractorService>.Instance),
        new WebSubService(null!, new Factory(), Microsoft.Extensions.Options.Options.Create(new WebSubOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSubService>.Instance),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<FeedAggregatorService>.Instance);

    [Fact]
    public async Task A_forced_poll_such_as_the_background_watchers_announces_its_result_to_open_pages()
    {
        using var env = new TempEnvironment();
        var feeds = Aggregator(env);
        var received = new List<FeedResult>();
        feeds.CacheUpdated += received.Add;

        var result = await feeds.GetAsync(force: true);

        var announced = Assert.Single(received);
        Assert.Same(result, announced);
        Assert.Same(result, feeds.PeekCache());
    }

    [Fact]
    public async Task Serving_a_fresh_cache_announces_nothing_because_nothing_changed()
    {
        using var env = new TempEnvironment();
        var feeds = Aggregator(env);
        await feeds.GetAsync(force: true);
        var raised = 0;
        feeds.CacheUpdated += _ => raised++;

        await feeds.GetAsync();
        await feeds.GetAsync();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Every_new_poll_announces_again()
    {
        using var env = new TempEnvironment();
        var feeds = Aggregator(env);
        var raised = 0;
        feeds.CacheUpdated += _ => raised++;

        await feeds.GetAsync(force: true);
        await feeds.GetAsync(force: true);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task A_page_that_throws_when_told_cannot_break_the_poll_or_the_other_listeners()
    {
        using var env = new TempEnvironment();
        var feeds = Aggregator(env);
        var healthyListenerHeard = false;
        feeds.CacheUpdated += _ => throw new InvalidOperationException("a page blew up");
        feeds.CacheUpdated += _ => healthyListenerHeard = true;

        var result = await feeds.GetAsync(force: true);

        Assert.NotNull(result);
        Assert.NotNull(feeds.PeekCache());
        Assert.True(healthyListenerHeard, "a listener registered after the broken one must still be told");
    }

    [Fact]
    public async Task A_page_that_has_unsubscribed_is_no_longer_told()
    {
        using var env = new TempEnvironment();
        var feeds = Aggregator(env);
        var raised = 0;
        void Handler(FeedResult _) => raised++;
        feeds.CacheUpdated += Handler;
        await feeds.GetAsync(force: true);

        feeds.CacheUpdated -= Handler;
        await feeds.GetAsync(force: true);

        Assert.Equal(1, raised);
    }
}
