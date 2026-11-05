using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Productivity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiPulse.Tests;

public class DigestEmailTests : IDisposable
{
    private readonly TestProductivityDb _prodDb = new();
    private readonly TempEnvironment _env = new();
    private readonly string _usersDbPath = Path.Combine(Path.GetTempPath(), "aipulse-users-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly FakeClock _clock = new(Items.Now);
    private readonly FakeEmailSender _email = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    private sealed class UsersFactory : IDbContextFactory<AiPulseDbContext>
    {
        private readonly string _cs;
        public UsersFactory(string path) => _cs = $"Data Source={path}";
        public AiPulseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite(_cs).Options);
    }

    public void Dispose()
    {
        _prodDb.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_usersDbPath); } catch { }
        _env.Dispose();
    }

    private async Task<(WeeklyDigestSender Sender, ProductivitySettingsService Settings, FeedHistoryService History, UserService Users)> MakeAsync(string? baseUrl = "https://pulse.example.test")
    {
        var users = new UserService(new UsersFactory(_usersDbPath), Options.Create(new AuthOptions { Username = "" }), _email, NullLogger<UserService>.Instance);
        await users.EnsureSchemaAndBootstrapAsync();
        var settings = new ProductivitySettingsService(_prodDb.Db);
        var history = new FeedHistoryService(_env);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var sender = new WeeklyDigestSender(settings, users, history, _email, new DigestUnsubscribeTokens(_dp),
            Options.Create(new DigestOptions { BaseUrl = baseUrl ?? "" }), config, _clock, NullLogger<WeeklyDigestSender>.Instance);
        return (sender, settings, history, users);
    }

    private static async Task<int> AddUser(UserService users, string name, string? email)
    {
        var (ok, error) = await users.RegisterAsync(name, "Fixture-Pass-1234!", email);
        Assert.True(ok, error);
        return (await users.GetByUsernameAsync(name))!.Id;
    }

    private int _seeded;

    /// <summary>A fresh two-source story three hours before the fake clock (first one links to /big-story).</summary>
    private void SeedNews(FeedHistoryService history)
    {
        var link = _seeded++ == 0 ? "https://example.test/big-story" : $"https://example.test/big-story-{_seeded}";
        history.Record(new[] { Items.Make("Big story", link: link, alsoSeenOn: new[] { "B" }, published: _clock.Now.AddHours(-3)) });
    }

    [Fact]
    public async Task Opted_in_users_get_one_email_per_week_with_text_html_and_unsubscribe()
    {
        var (sender, settings, history, users) = await MakeAsync();
        _clock.Now = DateTimeOffset.UtcNow;
        SeedNews(history);
        var alice = await AddUser(users, "alice", "alice@example.test");
        await settings.SetDigestAsync(alice, true);

        Assert.Equal(1, await sender.RunOnceAsync());
        var mail = Assert.Single(_email.Messages);
        Assert.Equal("alice@example.test", mail.To);
        Assert.StartsWith("This week in AI", mail.Subject);
        Assert.Contains("Big story", mail.Text);
        Assert.Contains("<a href=\"https://example.test/big-story\">Big story</a>", mail.Html);
        Assert.Contains("https://pulse.example.test/digest/unsubscribe?token=", mail.Text);
        Assert.StartsWith("<https://pulse.example.test/digest/unsubscribe?token=", mail.Headers!["List-Unsubscribe"]);

        // Same pass again, a day later and six days later: nothing.
        Assert.Equal(0, await sender.RunOnceAsync());
        _clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, await sender.RunOnceAsync());
        // A week later: the next one.
        _clock.Advance(TimeSpan.FromDays(1));
        SeedNews(history);
        Assert.Equal(1, await sender.RunOnceAsync());
        Assert.Equal(2, _email.Messages.Count);
    }

    [Fact]
    public async Task Users_without_email_or_not_opted_in_get_nothing()
    {
        var (sender, settings, history, users) = await MakeAsync();
        _clock.Now = DateTimeOffset.UtcNow;
        SeedNews(history);
        var noEmail = await AddUser(users, "noemail", null);
        await AddUser(users, "notopted", "n@example.test");
        await settings.SetDigestAsync(noEmail, true);
        Assert.Equal(0, await sender.RunOnceAsync());
        Assert.Empty(_email.Messages);
    }

    [Fact]
    public async Task An_empty_week_sends_nothing_and_does_not_use_up_the_week()
    {
        var (sender, settings, history, users) = await MakeAsync();
        _clock.Now = DateTimeOffset.UtcNow;
        var alice = await AddUser(users, "alice", "alice@example.test");
        await settings.SetDigestAsync(alice, true);
        Assert.Equal(0, await sender.RunOnceAsync());
        Assert.Null((await settings.GetAsync(alice)).DigestLastSentUtc);
        SeedNews(history);
        Assert.Equal(1, await sender.RunOnceAsync());
    }

    [Fact]
    public async Task Re_subscribing_within_the_week_does_not_send_again()
    {
        var (sender, settings, history, users) = await MakeAsync();
        _clock.Now = DateTimeOffset.UtcNow;
        SeedNews(history);
        var alice = await AddUser(users, "alice", "alice@example.test");
        await settings.SetDigestAsync(alice, true);
        await sender.RunOnceAsync();
        await settings.SetDigestAsync(alice, false);
        await settings.SetDigestAsync(alice, true);
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await sender.RunOnceAsync());
        Assert.Single(_email.Messages);
    }

    [Fact]
    public async Task Unsubscribe_token_opts_out_and_old_or_forged_tokens_do_not_work()
    {
        var (sender, settings, history, users) = await MakeAsync();
        _clock.Now = DateTimeOffset.UtcNow;
        SeedNews(history);
        var alice = await AddUser(users, "alice", "alice@example.test");
        await settings.SetDigestAsync(alice, true);
        await sender.RunOnceAsync();
        var url = _email.Messages[0].Text.Split('\n').First(l => l.Contains("Unsubscribe: ")).Split("Unsubscribe: ")[1].Trim();
        var token = Uri.UnescapeDataString(url.Split("token=")[1]);

        Assert.False(await sender.UnsubscribeAsync("forged-token"));
        Assert.False(await sender.UnsubscribeAsync(null));
        Assert.True(await sender.UnsubscribeAsync(token));
        Assert.False((await settings.GetAsync(alice)).DigestOptIn);

        // Re-subscribing rotates the nonce, so the old link no longer unsubscribes.
        await settings.SetDigestAsync(alice, true);
        Assert.False(await sender.UnsubscribeAsync(token));
        Assert.True((await settings.GetAsync(alice)).DigestOptIn);
    }

    [Fact]
    public void Tokens_from_another_purpose_or_key_ring_are_rejected()
    {
        var tokens = new DigestUnsubscribeTokens(_dp);
        var other = new DigestUnsubscribeTokens(new EphemeralDataProtectionProvider());
        var t = tokens.Create(5, "abc");
        Assert.Equal((5, "abc"), tokens.Read(t));
        Assert.Null(other.Read(t));
        Assert.Null(tokens.Read(_dp.CreateProtector("something-else").Protect("5:abc")));
    }

    [Theory]
    [InlineData("https://pulse.example.test/", null, "https://pulse.example.test")]
    [InlineData("", "https://hub.example.test/base/", "https://hub.example.test/base")]
    [InlineData("not a url", null, "http://localhost:5000")]
    [InlineData("ftp://x.test", null, "http://localhost:5000")]
    public void Base_url_resolution(string configured, string? fallback, string expected)
    {
        Assert.Equal(expected, WeeklyDigestSender.ResolveBaseUrl(configured, fallback));
    }

    [Fact]
    public void Schedule_is_at_most_weekly_with_a_little_slack()
    {
        var now = Items.Now.UtcDateTime;
        Assert.False(DigestSchedule.IsDue(new UserProductivitySettings { DigestOptIn = false }, now));
        Assert.True(DigestSchedule.IsDue(new UserProductivitySettings { DigestOptIn = true }, now));
        Assert.False(DigestSchedule.IsDue(new UserProductivitySettings { DigestOptIn = true, DigestLastSentUtc = now.AddDays(-6) }, now));
        Assert.True(DigestSchedule.IsDue(new UserProductivitySettings { DigestOptIn = true, DigestLastSentUtc = now.AddDays(-7).AddMinutes(30) }, now));
        Assert.False(DigestSchedule.IsDue(new UserProductivitySettings { DigestOptIn = true, DigestLastSentUtc = now.AddDays(-7).AddHours(2) }, now));
    }

    [Fact]
    public async Task The_default_interface_method_falls_back_to_plain_text_for_simple_senders()
    {
        IEmailSender plain = new NullEmailSender(NullLogger<NullEmailSender>.Instance);
        await plain.SendAsync("a@example.test", "s", "text", "<p>html</p>", null); // must not throw
    }
}
