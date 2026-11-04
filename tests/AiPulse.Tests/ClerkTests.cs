using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiPulse.Tests;

public class ClerkKeyTests
{
    private static string Key(string host, string kind = "test", bool pad = false)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(host + "$"));
        return $"pk_{kind}_" + (pad ? b64 : b64.TrimEnd('='));
    }

    [Theory]
    [InlineData("fancy-cat-12.clerk.accounts.dev", "test", false)]
    [InlineData("fancy-cat-12.clerk.accounts.dev", "test", true)]
    [InlineData("clerk.example.com", "live", false)]
    public void Reads_the_frontend_host_out_of_the_publishable_key(string host, string kind, bool pad)
    {
        Assert.True(ClerkKey.TryGetFrontendHost(Key(host, kind, pad), out var got));
        Assert.Equal(host, got);
        Assert.Equal("https://" + host, ClerkKey.Issuer(got));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    [InlineData("sk_test_abc")]                 // a SECRET key must never be accepted as the publishable one
    [InlineData("pk_dev_Zm9vLmJhciQ")]          // unknown environment
    [InlineData("pk_test_!!!notbase64!!!")]
    public void Rejects_things_that_are_not_publishable_keys(string input)
    {
        Assert.False(ClerkKey.TryGetFrontendHost(input, out var host));
        Assert.Equal("", host);
    }

    [Fact]
    public void Rejects_hosts_that_could_smuggle_a_url_or_path()
    {
        foreach (var bad in new[] { "evil.com/x", "evil.com:8080", "a b.com", "user@evil.com", "nodot" })
            Assert.False(ClerkKey.TryGetFrontendHost(Key(bad), out _), bad);
        // A key without the "$" terminator is not one of Clerk's.
        Assert.False(ClerkKey.TryGetFrontendHost("pk_test_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("good.example.com")), out _));
    }
}

public class ClerkTokenValidatorTests
{
    private const string Host = "fancy-cat-12.clerk.accounts.dev";
    private const string Origin = "https://aipulse.example";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A fake Clerk instance: a signing key, its public JWKS, and a way to mint tokens.</summary>
    private sealed class FakeClerk
    {
        public RSA Key { get; } = RSA.Create(2048);
        public string Kid { get; set; } = "ins_test_1";
        public int JwksFetches;

        public string Jwks() => Jwks(Key, Kid);

        public static string Jwks(RSA key, string kid)
        {
            var p = key.ExportParameters(false);
            return JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid, use = "sig", alg = "RS256", n = B64(p.Modulus!), e = B64(p.Exponent!) } } });
        }

        public string Token(Action<Dictionary<string, object?>>? tweak = null, string alg = "RS256", string? kid = null, RSA? signWith = null)
        {
            var claims = new Dictionary<string, object?>
            {
                ["iss"] = "https://" + Host, ["sub"] = "user_abc123", ["azp"] = Origin,
                ["exp"] = Now.AddSeconds(60).ToUnixTimeSeconds(), ["nbf"] = Now.AddSeconds(-5).ToUnixTimeSeconds(), ["iat"] = Now.ToUnixTimeSeconds(),
                ["email"] = "Ada@Example.com", ["email_verified"] = "true", ["name"] = "Ada Lovelace"
            };
            tweak?.Invoke(claims);
            var head = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg, typ = "JWT", kid = kid ?? Kid }));
            var body = B64(JsonSerializer.SerializeToUtf8Bytes(claims.Where(c => c.Value is not null).ToDictionary(c => c.Key, c => c.Value)));
            var sig = alg == "RS256" ? B64((signWith ?? Key).SignData(Encoding.ASCII.GetBytes($"{head}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) : "";
            return $"{head}.{body}.{sig}";
        }

        public ClerkTokenValidator Validator(Clock? clock = null) =>
            new((url, _) =>
            {
                Interlocked.Increment(ref JwksFetches);
                Assert.Equal($"https://{Host}/.well-known/jwks.json", url);
                return Task.FromResult(Jwks());
            }, clock ?? new Clock(Now));
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task A_genuine_token_is_accepted_and_yields_the_identity()
    {
        var clerk = new FakeClerk();
        var result = await clerk.Validator().ValidateAsync(clerk.Token(), Host, Origin);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("user_abc123", result.Identity!.Subject);
        Assert.Equal("Ada@Example.com", result.Identity.Email);
        Assert.True(result.Identity.EmailVerified);   // Clerk templates emit the string "true"
        Assert.Equal("Ada Lovelace", result.Identity.Name);
    }

    [Fact]
    public async Task Email_is_not_trusted_unless_the_token_says_it_is_verified()
    {
        var clerk = new FakeClerk();
        var missing = await clerk.Validator().ValidateAsync(clerk.Token(c => c.Remove("email_verified")), Host, Origin);
        var falseStr = await clerk.Validator().ValidateAsync(clerk.Token(c => c["email_verified"] = "false"), Host, Origin);
        var realBool = await clerk.Validator().ValidateAsync(clerk.Token(c => c["email_verified"] = true), Host, Origin);

        Assert.False(missing.Identity!.EmailVerified);
        Assert.False(falseStr.Identity!.EmailVerified);
        Assert.True(realBool.Identity!.EmailVerified);
    }

    [Fact]
    public async Task A_token_signed_by_somebody_elses_key_is_refused_even_with_a_valid_looking_kid()
    {
        var clerk = new FakeClerk();
        using var attacker = RSA.Create(2048);
        var forged = clerk.Token(signWith: attacker);

        var result = await clerk.Validator().ValidateAsync(forged, Host, Origin);

        Assert.False(result.Ok);
        Assert.Equal("Bad signature.", result.Error);
    }

    [Fact]
    public async Task A_tampered_payload_is_refused()
    {
        var clerk = new FakeClerk();
        var parts = clerk.Token().Split('.');
        var evil = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "https://" + Host, ["sub"] = "user_admin", ["azp"] = Origin, ["exp"] = Now.AddHours(5).ToUnixTimeSeconds()
        }));

        var result = await clerk.Validator().ValidateAsync($"{parts[0]}.{evil}.{parts[2]}", Host, Origin);

        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS512")]
    public async Task Only_RS256_is_accepted_so_algorithm_confusion_does_not_work(string alg)
    {
        var clerk = new FakeClerk();
        var result = await clerk.Validator().ValidateAsync(clerk.Token(alg: alg), Host, Origin);

        Assert.False(result.Ok);
        Assert.Equal("Unsupported algorithm.", result.Error);
    }

    [Fact]
    public async Task Expired_and_not_yet_valid_tokens_are_refused_with_a_little_clock_slack()
    {
        var clerk = new FakeClerk();
        var validator = clerk.Validator();

        Assert.False((await validator.ValidateAsync(clerk.Token(c => c["exp"] = Now.AddSeconds(-120).ToUnixTimeSeconds()), Host, Origin)).Ok);
        Assert.True((await validator.ValidateAsync(clerk.Token(c => c["exp"] = Now.AddSeconds(-10).ToUnixTimeSeconds()), Host, Origin)).Ok);   // within slack
        Assert.False((await validator.ValidateAsync(clerk.Token(c => c["nbf"] = Now.AddMinutes(10).ToUnixTimeSeconds()), Host, Origin)).Ok);
        Assert.False((await validator.ValidateAsync(clerk.Token(c => c.Remove("exp")), Host, Origin)).Ok);                                      // no expiry at all
    }

    [Fact]
    public async Task A_token_from_another_instance_or_for_another_site_is_refused()
    {
        var clerk = new FakeClerk();
        var validator = clerk.Validator();

        var otherIssuer = await validator.ValidateAsync(clerk.Token(c => c["iss"] = "https://evil.clerk.accounts.dev"), Host, Origin);
        var otherSite = await validator.ValidateAsync(clerk.Token(c => c["azp"] = "https://evil.example"), Host, Origin);
        var noAzp = await validator.ValidateAsync(clerk.Token(c => c.Remove("azp")), Host, Origin);
        var trailingSlash = await validator.ValidateAsync(clerk.Token(c => c["azp"] = Origin + "/"), Host, Origin);
        var anyOrigin = await validator.ValidateAsync(clerk.Token(c => c["azp"] = "https://evil.example"), Host, expectedOrigin: null);

        Assert.Equal("Wrong issuer.", otherIssuer.Error);
        Assert.Equal("Token was issued for a different site.", otherSite.Error);
        Assert.True(noAzp.Ok);
        Assert.True(trailingSlash.Ok);
        Assert.True(anyOrigin.Ok);
    }

    [Fact]
    public async Task A_token_without_a_subject_is_refused()
    {
        var clerk = new FakeClerk();
        var result = await clerk.Validator().ValidateAsync(clerk.Token(c => c.Remove("sub")), Host, Origin);
        Assert.Equal("No subject.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???.***")]
    public async Task Garbage_is_refused_without_throwing(string? token)
    {
        var clerk = new FakeClerk();
        var result = await clerk.Validator().ValidateAsync(token, Host, Origin);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task Signing_keys_are_fetched_once_and_reused()
    {
        var clerk = new FakeClerk();
        var validator = clerk.Validator();

        for (var i = 0; i < 5; i++) Assert.True((await validator.ValidateAsync(clerk.Token(), Host, Origin)).Ok);

        Assert.Equal(1, clerk.JwksFetches);
    }

    [Fact]
    public async Task A_rotated_key_is_picked_up_but_junk_key_ids_cannot_hammer_Clerk()
    {
        var clock = new Clock(Now);
        var clerk = new FakeClerk();
        var validator = clerk.Validator(clock);
        Assert.True((await validator.ValidateAsync(clerk.Token(), Host, Origin)).Ok);   // fetch #1

        // Junk kids right away: refused, and no extra fetch.
        for (var i = 0; i < 20; i++) Assert.False((await validator.ValidateAsync(clerk.Token(kid: "junk" + i), Host, Origin)).Ok);
        Assert.Equal(1, clerk.JwksFetches);

        // Clerk rotates its key; once the minimum gap has passed the new kid is found with one more fetch.
        clock.Now = Now.AddMinutes(3);
        clerk.Kid = "ins_test_2";
        var rotated = clerk.Token(c => { c["exp"] = clock.Now.AddSeconds(60).ToUnixTimeSeconds(); c["nbf"] = clock.Now.AddSeconds(-5).ToUnixTimeSeconds(); });
        Assert.True((await validator.ValidateAsync(rotated, Host, Origin)).Ok);
        Assert.Equal(2, clerk.JwksFetches);
    }

    // ---- the whole sign-in: validator + admin switch + account linking ----

    private sealed class Rig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "aipulse-clerk-" + Guid.NewGuid().ToString("N"));
        public readonly FakeClerk Clerk = new();
        public readonly IDbContextFactory<AiPulseDbContext> Db;
        public readonly UserService Users;
        public readonly OAuthSettingsService Settings;
        public readonly ClerkAuthService Auth;

        public Rig(bool requireApproval = false)
        {
            Directory.CreateDirectory(Dir);
            var options = new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite($"Data Source={Path.Combine(Dir, "t.db")}").Options;
            Db = new SimpleFactory(options);
            Users = new UserService(Db, Options.Create(new AuthOptions { Username = "admin", Password = "Passw0rd!x", RequireApproval = requireApproval }),
                new NullEmail(), NullLogger<UserService>.Instance);
            Users.EnsureSchemaAndBootstrapAsync().GetAwaiter().GetResult();
            Settings = new OAuthSettingsService(Db, new OptionsCache<GoogleOptions>(), new OptionsCache<AspNet.Security.OAuth.GitHub.GitHubAuthenticationOptions>(), new EphemeralDataProtectionProvider());
            Auth = new ClerkAuthService(Settings, Clerk.Validator(), new ExternalAuthService(Db, Users));
        }

        public static string PublishableKey => "pk_test_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Host + "$")).TrimEnd('=');
        public Task Enable(bool on = true) => Settings.SaveAsync(ClerkAuthService.Provider, on, PublishableKey, "");

        public void Dispose()
        {
            SqliteCleanup();
            try { Directory.Delete(Dir, true); } catch (IOException) { /* temp folder; the OS will clear it */ }
        }

        private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        private sealed class SimpleFactory(DbContextOptions<AiPulseDbContext> options) : IDbContextFactory<AiPulseDbContext>
        {
            public AiPulseDbContext CreateDbContext() => new(options);
        }

        private sealed class NullEmail : IEmailSender
        {
            public bool Enabled => false;
            public Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Nobody_can_sign_in_with_Clerk_until_an_admin_turns_it_on()
    {
        using var rig = new Rig();
        var (result, user, error) = await rig.Auth.SignInAsync(rig.Clerk.Token(), Origin);

        Assert.Equal(ExternalLoginResult.Disabled, result);
        Assert.Null(user);
        Assert.Equal("Clerk sign-in is not enabled.", error);
        Assert.Null(await rig.Auth.GetActiveAsync());
    }

    [Fact]
    public async Task The_admin_switch_works_both_ways_and_a_bad_key_keeps_it_off()
    {
        using var rig = new Rig();
        await rig.Enable();
        Assert.Equal(Host, (await rig.Auth.GetActiveAsync())!.Value.Host);

        await rig.Enable(false);
        Assert.Null(await rig.Auth.GetActiveAsync());

        await rig.Settings.SaveAsync(ClerkAuthService.Provider, true, "sk_test_secretsecret", "");
        Assert.Null(await rig.Auth.GetActiveAsync());
    }

    [Fact]
    public async Task A_new_Clerk_person_gets_a_local_account_and_the_same_person_signs_back_into_it()
    {
        using var rig = new Rig();
        await rig.Enable();

        var (first, user1, _) = await rig.Auth.SignInAsync(rig.Clerk.Token(), Origin);
        var (second, user2, _) = await rig.Auth.SignInAsync(rig.Clerk.Token(c => c["email"] = "changed@example.com"), Origin);

        Assert.Equal(ExternalLoginResult.Success, first);
        Assert.Equal("adalovelace", user1!.Username);
        Assert.Equal(UserRoles.User, user1.Role);
        Assert.True(user1.EmailVerified);
        Assert.Equal(ExternalLoginResult.Success, second);
        Assert.Equal(user1.Id, user2!.Id);   // keyed on Clerk's user id, not on the email that changed
    }

    [Fact]
    public async Task A_verified_email_attaches_the_Clerk_login_to_the_matching_local_account_and_keeps_its_role()
    {
        using var rig = new Rig();
        await rig.Enable();
        await using (var db = await rig.Db.CreateDbContextAsync())
        {
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            admin.Email = "boss@example.com";
            await db.SaveChangesAsync();
        }

        var (result, user, _) = await rig.Auth.SignInAsync(rig.Clerk.Token(c => c["email"] = "BOSS@example.com"), Origin);

        Assert.Equal(ExternalLoginResult.Success, result);
        Assert.Equal("admin", user!.Username);
        Assert.Equal(UserRoles.Admin, user.Role);
    }

    [Fact]
    public async Task An_UNVERIFIED_email_never_attaches_to_an_existing_account_so_typing_the_admins_address_is_useless()
    {
        using var rig = new Rig();
        await rig.Enable();
        await using (var db = await rig.Db.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(u => u.Username == "admin")).Email = "boss@example.com";
            await db.SaveChangesAsync();
        }

        var (result, user, _) = await rig.Auth.SignInAsync(
            rig.Clerk.Token(c => { c["email"] = "boss@example.com"; c["email_verified"] = "false"; c["sub"] = "user_impostor"; }), Origin);

        Assert.Equal(ExternalLoginResult.Success, result);
        Assert.NotEqual("admin", user!.Username);
        Assert.Equal(UserRoles.User, user.Role);
        Assert.False(user.EmailVerified);
    }

    [Fact]
    public async Task Approval_and_disabled_accounts_are_enforced_for_Clerk_people_too()
    {
        using var approval = new Rig(requireApproval: true);
        await approval.Enable();
        var (pending, none, _) = await approval.Auth.SignInAsync(approval.Clerk.Token(), Origin);
        Assert.Equal(ExternalLoginResult.PendingApproval, pending);
        Assert.Null(none);

        using var rig = new Rig();
        await rig.Enable();
        var (_, user, _) = await rig.Auth.SignInAsync(rig.Clerk.Token(), Origin);
        await using (var db = await rig.Db.CreateDbContextAsync())
        {
            (await db.Users.FindAsync(user!.Id))!.Status = UserStatuses.Disabled;
            await db.SaveChangesAsync();
        }
        var (disabled, _, _) = await rig.Auth.SignInAsync(rig.Clerk.Token(), Origin);
        Assert.Equal(ExternalLoginResult.Disabled, disabled);
    }

    [Fact]
    public async Task A_forged_token_creates_no_account()
    {
        using var rig = new Rig();
        await rig.Enable();
        using var attacker = RSA.Create(2048);

        var (result, user, error) = await rig.Auth.SignInAsync(rig.Clerk.Token(signWith: attacker), Origin);

        Assert.Equal(ExternalLoginResult.Disabled, result);
        Assert.Null(user);
        Assert.Equal("Bad signature.", error);
        await using var db = await rig.Db.CreateDbContextAsync();
        Assert.Equal(1, await db.Users.CountAsync());   // just the seeded admin
    }
}
