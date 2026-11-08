using AiPulse.Models;
using AiPulse.Services.Ops;
using Xunit;

namespace AiPulse.Tests;

public class OpsTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FeedItem Item(string link) => new() { Title = link, Link = link, SourceName = "s", Category = "News" };

    [Fact]
    public void Tracker_counts_sources_duration_failures_and_skips()
    {
        var time = new ManualTime(T0);
        var tracker = new PollStatusTracker(time);

        time.Now = T0.AddSeconds(10);
        tracker.OnSourceFinished(Array.Empty<FeedItem>());
        Assert.True(tracker.Snapshot().InProgress);
        tracker.OnSourceFinished(Array.Empty<FeedItem>());
        tracker.OnSourceFinished(Array.Empty<FeedItem>());
        time.Now = T0.AddSeconds(40);
        tracker.OnResultPublished(new FeedResult { Items = { Item("a"), Item("b") }, Errors = { "x: boom" }, FetchedAt = time.Now });

        var s = tracker.Snapshot();
        Assert.False(s.InProgress);
        Assert.Equal(1, s.PollsCompleted);
        Assert.Equal(3, s.LastFetched);
        Assert.Equal(1, s.LastFailed);
        Assert.Equal(2, s.LastItems);
        Assert.Equal(TimeSpan.FromSeconds(30), s.LastDuration);
        Assert.Equal(new[] { "x: boom" }, s.LastErrors);
    }

    [Fact]
    public void Tracker_tells_a_websub_merge_from_a_poll()
    {
        var time = new ManualTime(T0);
        var tracker = new PollStatusTracker(time);
        var fetchedAt = T0.AddSeconds(5);
        tracker.OnSourceFinished(Array.Empty<FeedItem>());
        tracker.OnResultPublished(new FeedResult { Items = { Item("a") }, FetchedAt = fetchedAt });

        // A WebSub push republishes the cache with the poll's own FetchedAt.
        time.Now = T0.AddMinutes(3);
        tracker.OnResultPublished(new FeedResult { Items = { Item("a"), Item("b") }, FetchedAt = fetchedAt });

        var s = tracker.Snapshot();
        Assert.Equal(1, s.PollsCompleted);
        Assert.Equal(1, s.WebSubMerges);
        Assert.Equal(time.Now, s.LastWebSubMergeAt);
        Assert.Equal(2, s.LastItems);
    }

    [Fact]
    public void Tracker_prefers_the_aggregators_reported_round_counts()
    {
        var tracker = new PollStatusTracker(new ManualTime(T0));
        tracker.ReportRound(round: 7, due: 10, skipped: 12);
        tracker.OnSourceFinished(Array.Empty<FeedItem>());
        tracker.OnResultPublished(new FeedResult { FetchedAt = T0 });

        var s = tracker.Snapshot();
        Assert.Equal(7, s.ReportedRound);
        Assert.Equal(10, s.LastFetched);
        Assert.Equal(12, s.LastSkipped);
    }

    [Fact]
    public void Next_scheduled_poll_follows_the_watchers_fixed_timer()
    {
        var time = new ManualTime(T0);
        var tracker = new PollStatusTracker(time);
        var interval = TimeSpan.FromMinutes(15);

        Assert.Equal(T0.AddSeconds(5), tracker.NextScheduledPoll(interval));
        time.Now = T0.AddMinutes(1);
        Assert.Equal(T0.AddSeconds(5) + interval, tracker.NextScheduledPoll(interval));
        time.Now = T0.AddMinutes(31);
        Assert.Equal(T0.AddSeconds(5) + 3 * interval, tracker.NextScheduledPoll(interval));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-2", "'-2")]
    [InlineData(null, "")]
    public void Csv_cells_are_quoted_and_cannot_run_as_spreadsheet_formulas(string? value, string expected) =>
        Assert.Equal(expected, AuditLogQueryService.Csv(value));

    [Fact]
    public void Audit_filter_parses_and_round_trips_the_query_string()
    {
        var f = AuditLogFilter.FromQuery(" Login.Failed ", "bob", "2026-01-02", "not-a-date");
        Assert.Equal("Login.Failed", f.Action);
        Assert.Equal("bob", f.User);
        Assert.Equal(new DateOnly(2026, 1, 2), f.From);
        Assert.Null(f.To);
        Assert.Equal("action=Login.Failed&user=bob&from=2026-01-02", f.ToQueryString());
        Assert.Equal("", new AuditLogFilter().ToQueryString());
    }

    [Theory]
    [InlineData("aipulse-backup-20260101-120000.zip", true)]
    [InlineData("aipulse-backup-20260101-120000-2.zip", true)]
    [InlineData("../aipulse-backup-20260101-120000.zip", false)]
    [InlineData("aipulse-backup-20260101-120000.zip.partial", false)]
    [InlineData("appsettings.json", false)]
    [InlineData(null, false)]
    public void Only_our_own_backup_names_are_accepted(string? name, bool ok) =>
        Assert.Equal(ok, OpsBackupStore.IsValidName(name));

    [Fact]
    public void Version_info_has_a_version()
    {
        var (version, _) = OpsStatusService.VersionInfo();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
