using System.Net;
using System.Security.Claims;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Tests.E2E;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiPulse.Tests;

public class SocialLoginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "social-" + Guid.NewGuid().ToString("N"));
    private readonly IDbContextFactory<AiPulseDbContext> _db;
    private readonly UserService _users;

    public SocialLoginTests()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "t.db")}").Options;
        _db = new SimpleFactory(options);
        _users = new UserService(_db, Options.Create(new AuthOptions { Username = "admin", Password = "Passw0rd!x" }), new NoEmail(), NullLogger<UserService>.Instance);
        _users.EnsureSchemaAndBootstrapAsync().GetAwaiter().GetResult();
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class SimpleFactory(DbContextOptions<AiPulseDbContext> o) : IDbContextFactory<AiPulseDbContext> { public AiPulseDbContext CreateDbContext() => new(o); }
    private sealed class NoEmail : IEmailSender { public bool Enabled => false; public Task SendAsync(string to, string s, string b, CancellationToken ct = default) => Task.CompletedTask; }

    private ExternalAuthService Service(params string[] adminEmails) => new(_db, _users, Options.Create(new AuthOptions { AdminEmails = adminEmails }));

    private static ClaimsPrincipal Principal(string subject, string? email, string? name = "Ada Lovelace") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Email, email ?? ""), new Claim(ClaimTypes.Name, name ?? "")
        }.Where(c => c.Value.Length > 0), "test"));

    private async Task<AppUser> UserAsync(string name) => (await _users.GetByUsernameAsync(name))!;

    // ---- providers ----

    [Fact]
    public void Google_GitHub_and_Facebook_are_the_configurable_providers_in_that_order()
    {
        Assert.Equal(new[] { "Google", "GitHub", "Facebook" }, OAuthProviders.All);
    }

    [Fact]
    public async Task Settings_lists_a_form_for_every_provider_even_before_anything_is_saved()
    {
        var settings = new OAuthSettingsService(_db, new OptionsCache<GoogleOptions>(), new OptionsCache<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions>(), new EphemeralDataProtectionProvider());
        var rows = await settings.GetAllAsync();

        Assert.Equal(new[] { "Google", "GitHub", "Facebook" }, rows.Select(r => r.Provider));
        Assert.All(rows, r => Assert.False(r.Enabled));
        Assert.False(await settings.IsUsableAsync(OAuthProviders.Facebook));

        await settings.SaveAsync(OAuthProviders.Facebook, true, "app-id-123", "app-secret-xyz");
        Assert.True(await settings.IsUsableAsync(OAuthProviders.Facebook));
        Assert.Equal("app-id-123", (await settings.GetAsync(OAuthProviders.Facebook))!.ClientId);
        Assert.Equal(3, (await settings.GetAllAsync()).Count);
    }

    [Fact]
    public async Task A_provider_with_an_id_but_no_secret_is_not_usable()
    {
        var settings = new OAuthSettingsService(_db, new OptionsCache<GoogleOptions>(), new OptionsCache<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions>(), new EphemeralDataProtectionProvider());
        await settings.SaveAsync(OAuthProviders.Facebook, true, "app-id-123", "");
        Assert.False(await settings.IsUsableAsync(OAuthProviders.Facebook));
    }

    // ---- Facebook sign-in ----

    [Fact]
    public async Task A_new_Facebook_person_gets_a_plain_account_with_an_unverified_email()
    {
        var (result, user) = await Service().CompleteAsync(OAuthProviders.Facebook, Principal("fb-1", "ada@example.com"), emailVerified: false);

        Assert.Equal(ExternalLoginResult.Success, result);
        Assert.Equal(UserRoles.User, user!.Role);
        Assert.False(user.EmailVerified);
        Assert.Equal("ada@example.com", user.Email);
    }

    [Fact]
    public async Task A_Facebook_email_never_attaches_to_an_existing_account()
    {
        await _users.CreateApprovedUserAsync("victim", "Victim-pass-1!", UserRoles.User);
        await using (var db = await _db.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(u => u.Username == "victim")).Email = "victim@example.com";
            await db.SaveChangesAsync();
        }

        var (_, user) = await Service().CompleteAsync(OAuthProviders.Facebook, Principal("fb-attacker", "victim@example.com", "Mallory"), emailVerified: false);

        Assert.NotEqual("victim", user!.Username);
    }

    [Fact]
    public async Task The_same_Facebook_person_signs_back_into_the_same_account()
    {
        var service = Service();
        var (_, first) = await service.CompleteAsync(OAuthProviders.Facebook, Principal("fb-9", "x@example.com"), emailVerified: false);
        var (_, second) = await service.CompleteAsync(OAuthProviders.Facebook, Principal("fb-9", "other@example.com"), emailVerified: false);
        Assert.Equal(first!.Id, second!.Id);
    }

    // ---- Auth:AdminEmails ----

    [Fact]
    public async Task A_verified_Google_address_on_the_admin_list_makes_a_new_account_an_admin()
    {
        var (result, user) = await Service("boss@example.com").CompleteAsync(OAuthProviders.Google, Principal("g-1", "Boss@Example.com"));

        Assert.Equal(ExternalLoginResult.Success, result);
        Assert.Equal(UserRoles.Admin, user!.Role);
        Assert.Equal(UserStatuses.Approved, user.Status);
        await using var db = await _db.CreateDbContextAsync();
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "Role.PromotedByConfig");
    }

    [Fact]
    public async Task An_existing_account_with_that_verified_address_is_promoted_too()
    {
        await _users.CreateApprovedUserAsync("owner", "Owner-pass-1!", UserRoles.User);
        await using (var db = await _db.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(u => u.Username == "owner")).Email = "owner@example.com";
            await db.SaveChangesAsync();
        }

        var (_, user) = await Service("owner@example.com").CompleteAsync(OAuthProviders.Google, Principal("g-2", "owner@example.com"));

        Assert.Equal("owner", user!.Username);
        Assert.Equal(UserRoles.Admin, (await UserAsync("owner")).Role);
    }

    [Fact]
    public async Task An_address_the_provider_does_not_verify_never_grants_admin()
    {
        var (_, facebook) = await Service("boss@example.com").CompleteAsync(OAuthProviders.Facebook, Principal("fb-2", "boss@example.com"), emailVerified: false);
        Assert.Equal(UserRoles.User, facebook!.Role);

        var (_, clerk) = await Service("boss@example.com").CompleteAsync("Clerk", Principal("user_1", "boss@example.com"), emailVerified: false);
        Assert.Equal(UserRoles.User, clerk!.Role);
    }

    [Fact]
    public async Task A_verified_Clerk_address_on_the_list_does_grant_admin()
    {
        var (_, user) = await Service("boss@example.com").CompleteAsync("Clerk", Principal("user_2", "boss@example.com"), emailVerified: true);
        Assert.Equal(UserRoles.Admin, user!.Role);
    }

    [Fact]
    public async Task Other_addresses_and_an_empty_list_change_nothing()
    {
        var (_, other) = await Service("boss@example.com").CompleteAsync(OAuthProviders.Google, Principal("g-3", "someone@example.com"));
        Assert.Equal(UserRoles.User, other!.Role);

        var (_, none) = await Service().CompleteAsync(OAuthProviders.Google, Principal("g-4", "boss@example.com"));
        Assert.Equal(UserRoles.User, none!.Role);
    }

    [Fact]
    public async Task A_disabled_account_is_not_resurrected_by_the_admin_list()
    {
        var service = Service("boss@example.com");
        var (_, user) = await service.CompleteAsync(OAuthProviders.Google, Principal("g-5", "boss@example.com"));
        await using (var db = await _db.CreateDbContextAsync())
        {
            var row = await db.Users.FindAsync(user!.Id);
            row!.Status = UserStatuses.Disabled;
            row.Role = UserRoles.User;
            await db.SaveChangesAsync();
        }

        var (result, _) = await service.CompleteAsync(OAuthProviders.Google, Principal("g-5", "boss@example.com"));

        Assert.Equal(ExternalLoginResult.Disabled, result);
        Assert.Equal(UserRoles.User, (await UserAsync(user.Username)).Role);
    }

    [Theory]
    [InlineData("a@b.co", "A@B.CO", true)]
    [InlineData(" a@b.co ", "a@b.co", true)]
    [InlineData("a@b.co", "c@b.co", false)]
    [InlineData("a@b.co", null, false)]
    [InlineData("a@b.co", "", false)]
    public void Admin_emails_match_without_regard_to_case_or_padding(string listed, string? email, bool expected) =>
        Assert.Equal(expected, new AuthOptions { AdminEmails = new[] { listed } }.IsAdminEmail(email));

    [Fact]
    public void The_developer_guide_and_Settings_name_the_same_callback_addresses_the_code_registers()
    {
        var program = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Program.cs"));
        var guide = File.ReadAllText(Path.Combine(TestData.RepoRoot, "docs", "DEVELOPER_GUIDE.md"));
        var settings = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Components", "Pages", "Settings.razor"));
        foreach (var provider in OAuthProviders.All)
        {
            var path = "/signin-" + provider.ToLowerInvariant();
            Assert.Contains($"CallbackPath = \"{path}\"", program);
            Assert.Contains(path, guide);
            Assert.Contains(path, settings);
        }
    }

    [Fact]
    public void Provider_sign_ins_are_exempt_from_the_local_two_step_screen_by_default()
    {
        var exempt = new AiPulse.Services.Security.SecurityHardeningOptions().TwoFactorExemptSignInPaths;
        foreach (var provider in OAuthProviders.All) Assert.Contains("/signin-" + provider.ToLowerInvariant(), exempt);
    }

    [Fact]
    public void The_list_can_come_from_the_environment()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:AdminEmails:0"] = "one@example.com", ["Auth:AdminEmails:1"] = "two@example.com"
        }).Build();
        var auth = config.GetSection("Auth").Get<AuthOptions>()!;
        Assert.True(auth.IsAdminEmail("two@example.com"));
    }
}

[Collection(E2ECollection.Name)]
public class SocialLoginEndpointTests(AiPulseAppFactory app)
{
    private OAuthSettingsService Settings => app.Services.GetRequiredService<OAuthSettingsService>();

    [Fact]
    public async Task Each_enabled_provider_shows_a_button_and_sends_the_browser_to_its_own_site_with_the_right_callback()
    {
        await Settings.SaveAsync(OAuthProviders.Facebook, true, "fb-app-123", "fb-secret");
        await Settings.SaveAsync(OAuthProviders.Google, true, "google-client-123.apps.test", "google-secret");
        await Settings.SaveAsync(OAuthProviders.GitHub, true, "gh-client-123", "gh-secret");
        try
        {
            var client = app.Anonymous();
            var login = await client.GetStringAsync("/login");
            Assert.Contains("/auth/external/facebook", login);
            Assert.Contains("/auth/external/google", login);
            Assert.Contains("/auth/external/github", login);

            foreach (var (provider, host, id, callback) in new[]
            {
                ("facebook", "facebook.com", "fb-app-123", "/signin-facebook"),
                ("google", "accounts.google.com", "google-client-123.apps.test", "/signin-google"),
                ("github", "github.com", "gh-client-123", "/signin-github"),
            })
            {
                var response = await client.GetAsync($"/auth/external/{provider}?returnUrl=%2Fnews");
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                var location = response.Headers.Location!.ToString();
                Assert.Contains(host, location);
                Assert.Contains(id, location);
                Assert.Contains(Uri.EscapeDataString("http://localhost" + callback), location);
            }
        }
        finally
        {
            foreach (var p in OAuthProviders.All) await Settings.SaveAsync(p, false, "", "");
        }
    }

    [Fact]
    public async Task A_provider_that_is_off_shows_no_button_and_the_start_address_goes_back_to_the_login_page()
    {
        var client = app.Anonymous();
        var login = await client.GetStringAsync("/login");
        Assert.DoesNotContain("/auth/external/facebook", login);

        var response = await client.GetAsync("/auth/external/facebook");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/external/myspace")).StatusCode);
    }
}
