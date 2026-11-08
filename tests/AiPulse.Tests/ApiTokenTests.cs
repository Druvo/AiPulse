using AiPulse.Models;
using AiPulse.Services.Api;
using Xunit;

namespace AiPulse.Tests;

public class ApiTokenFormatTests
{
    [Fact]
    public void Generated_tokens_have_the_marker_the_right_length_and_are_unique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ApiTokenFormat.Generate()).ToList();
        Assert.All(tokens, t =>
        {
            Assert.StartsWith("aip_", t);
            Assert.Equal(ApiTokenFormat.Length, t.Length);
            Assert.True(ApiTokenFormat.IsWellFormed(t));
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("aip_short")]
    [InlineData("xyz_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aip_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("aip_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    [InlineData("aip_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_are_rejected(string? token) => Assert.False(ApiTokenFormat.IsWellFormed(token));

    [Fact]
    public void Hash_is_lowercase_hex_sha256_and_deterministic()
    {
        var h = ApiTokenFormat.Hash("aip_example");
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.Equal(h, ApiTokenFormat.Hash("aip_example"));
        Assert.NotEqual(h, ApiTokenFormat.Hash("aip_examplf"));
        // Known vector: sha256("abc")
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ApiTokenFormat.Hash("abc"));
    }

    [Fact]
    public void Display_prefix_is_the_first_twelve_characters()
    {
        var t = ApiTokenFormat.Generate();
        Assert.Equal(t[..12], ApiTokenFormat.DisplayPrefix(t));
        Assert.StartsWith("aip_", ApiTokenFormat.DisplayPrefix(t));
    }

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer   abc  ", "abc")]
    [InlineData("BEARER abc", "abc")]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer", null)]
    [InlineData("Bearer ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Authorization_header_parsing(string? header, string? expected) =>
        Assert.Equal(expected, ApiTokenFormat.FromAuthorizationHeader(header));
}

public class ApiTokenServiceTests
{
    [Fact]
    public async Task Create_returns_the_token_once_and_stores_only_its_hash()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("alice");
        var (created, error) = await db.Tokens().CreateAsync(user.Id, "  laptop script ", TimeSpan.FromDays(30));

        Assert.Null(error);
        Assert.NotNull(created);
        Assert.Equal("laptop script", created!.Info.Name);
        Assert.Equal(ApiTokenFormat.DisplayPrefix(created.Token), created.Info.Prefix);
        Assert.Equal("read", created.Info.Scopes);

        // The plaintext is nowhere in the database; its hash is.
        Assert.Equal(0, await db.ScalarAsync($"SELECT COUNT(*) FROM ApiTokens WHERE TokenHash = '{created.Token}' OR Prefix = '{created.Token}' OR Name = '{created.Token}'"));
        Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM ApiTokens WHERE TokenHash = '{ApiTokenFormat.Hash(created.Token)}'"));
    }

    [Fact]
    public async Task Valid_token_resolves_to_its_user_and_records_last_use()
    {
        var time = new ManualTime();
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("bob", UserRoles.Curator);
        var tokens = db.Tokens(time);
        var (created, _) = await tokens.CreateAsync(user.Id, "ci", null);

        var result = await tokens.ValidateAsync(created!.Token);
        Assert.True(result.IsValid);
        Assert.Equal(user.Id, result.Principal!.UserId);
        Assert.Equal("bob", result.Principal.Username);
        Assert.Equal(UserRoles.Curator, result.Principal.Role);
        Assert.Equal(created.Info.Id, result.Principal.TokenId);
        Assert.Contains("read", result.Principal.Scopes);

        var listed = (await tokens.ListAsync(user.Id)).Single();
        Assert.Equal(time.GetUtcNow(), listed.LastUsedAt);
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        var time = new ManualTime();
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("carol");
        var tokens = db.Tokens(time);
        var (created, _) = await tokens.CreateAsync(user.Id, "x", null);

        await tokens.ValidateAsync(created!.Token);
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(30));
        await tokens.ValidateAsync(created.Token);
        Assert.Equal(first, (await tokens.ListAsync(user.Id)).Single().LastUsedAt);

        time.Advance(TimeSpan.FromSeconds(31));
        await tokens.ValidateAsync(created.Token);
        Assert.Equal(time.GetUtcNow(), (await tokens.ListAsync(user.Id)).Single().LastUsedAt);
    }

    [Fact]
    public async Task Expired_tokens_are_refused_and_listed_as_expired()
    {
        var time = new ManualTime();
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("dave");
        var tokens = db.Tokens(time);
        var (created, _) = await tokens.CreateAsync(user.Id, "short", TimeSpan.FromDays(7));
        Assert.Equal(time.GetUtcNow().AddDays(7), created!.Info.ExpiresAt);

        time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.True((await tokens.ValidateAsync(created.Token)).IsValid);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await tokens.ValidateAsync(created.Token);
        Assert.False(result.IsValid);
        Assert.Equal(ApiTokenFailure.Expired, result.Failure);
        Assert.True((await tokens.ListAsync(user.Id)).Single().IsExpired(time.GetUtcNow()));
    }

    [Fact]
    public async Task Revoked_tokens_are_refused_and_only_the_owner_can_revoke()
    {
        await using var db = await TempDb.CreateAsync();
        var owner = await db.UserAsync("erin");
        var other = await db.UserAsync("frank");
        var tokens = db.Tokens();
        var (created, _) = await tokens.CreateAsync(owner.Id, "k", null);

        Assert.False(await tokens.RevokeAsync(other.Id, created!.Info.Id));
        Assert.True((await tokens.ValidateAsync(created.Token)).IsValid);

        Assert.True(await tokens.RevokeAsync(owner.Id, created.Info.Id));
        Assert.False(await tokens.RevokeAsync(owner.Id, created.Info.Id)); // already revoked
        var result = await tokens.ValidateAsync(created.Token);
        Assert.Equal(ApiTokenFailure.Revoked, result.Failure);
        Assert.True((await tokens.ListAsync(owner.Id)).Single().IsRevoked);
    }

    [Fact]
    public async Task Revoke_all_revokes_only_that_users_active_tokens()
    {
        await using var db = await TempDb.CreateAsync();
        var a = await db.UserAsync("gina");
        var b = await db.UserAsync("hank");
        var tokens = db.Tokens();
        await tokens.CreateAsync(a.Id, "one", null);
        await tokens.CreateAsync(a.Id, "two", null);
        var (bToken, _) = await tokens.CreateAsync(b.Id, "keep", null);

        Assert.Equal(2, await tokens.RevokeAllAsync(a.Id));
        Assert.All(await tokens.ListAsync(a.Id), t => Assert.True(t.IsRevoked));
        Assert.True((await tokens.ValidateAsync(bToken!.Token)).IsValid);
    }

    [Fact]
    public async Task Tokens_of_disabled_or_deleted_accounts_stop_working()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("ivan");
        var tokens = db.Tokens();
        var (created, _) = await tokens.CreateAsync(user.Id, "k", null);

        await db.Users.SetStatusAsync(user.Id, UserStatuses.Disabled);
        Assert.Equal(ApiTokenFailure.UserInactive, (await tokens.ValidateAsync(created!.Token)).Failure);

        await db.Users.SetStatusAsync(user.Id, UserStatuses.Approved);
        Assert.True((await tokens.ValidateAsync(created.Token)).IsValid);

        await db.Users.DeleteAsync(user.Id);
        Assert.Equal(ApiTokenFailure.UserInactive, (await tokens.ValidateAsync(created.Token)).Failure);
    }

    [Fact]
    public async Task Unknown_malformed_and_missing_tokens_fail_with_their_reason()
    {
        await using var db = await TempDb.CreateAsync();
        var tokens = db.Tokens();
        Assert.Equal(ApiTokenFailure.Missing, (await tokens.ValidateAsync(null)).Failure);
        Assert.Equal(ApiTokenFailure.Missing, (await tokens.ValidateAsync("  ")).Failure);
        Assert.Equal(ApiTokenFailure.Malformed, (await tokens.ValidateAsync("not-a-token")).Failure);
        Assert.Equal(ApiTokenFailure.Unknown, (await tokens.ValidateAsync(ApiTokenFormat.Generate())).Failure);
    }

    [Fact]
    public async Task A_token_whose_hash_matches_but_differs_by_a_character_is_unknown()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("judy");
        var tokens = db.Tokens();
        var (created, _) = await tokens.CreateAsync(user.Id, "k", null);
        var t = created!.Token;
        var tampered = t[..^1] + (t[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(ApiTokenFailure.Unknown, (await tokens.ValidateAsync(tampered)).Failure);
    }

    [Fact]
    public async Task Scopes_other_than_read_are_rejected_at_creation_and_checked_at_validation()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("kim");
        var tokens = db.Tokens();
        var (bad, error) = await tokens.CreateAsync(user.Id, "w", null, new[] { "write" });
        Assert.Null(bad);
        Assert.Contains("scope", error, StringComparison.OrdinalIgnoreCase);

        var (ok, _) = await tokens.CreateAsync(user.Id, "r", null, new[] { "READ", "read" });
        Assert.Equal("read", ok!.Info.Scopes);
        Assert.Equal(ApiTokenFailure.InsufficientScope, (await tokens.ValidateAsync(ok.Token, "admin")).Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("tab\tname")]
    public async Task Bad_names_are_rejected(string? name)
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("lee");
        var (created, error) = await db.Tokens().CreateAsync(user.Id, name, null);
        Assert.Null(created);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Names_longer_than_sixty_characters_are_rejected()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("max");
        Assert.NotNull((await db.Tokens().CreateAsync(user.Id, new string('a', 61), null)).Error);
        Assert.Null((await db.Tokens().CreateAsync(user.Id, new string('a', 60), null)).Error);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(367)]
    public async Task Lifetimes_outside_one_day_to_one_year_are_rejected(double days)
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("ned");
        Assert.NotNull((await db.Tokens().CreateAsync(user.Id, "x", TimeSpan.FromDays(days))).Error);
    }

    [Fact]
    public async Task Duplicate_active_names_are_rejected_but_a_revoked_name_can_be_reused()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("olga");
        var tokens = db.Tokens();
        var (first, _) = await tokens.CreateAsync(user.Id, "ci", null);
        Assert.NotNull((await tokens.CreateAsync(user.Id, "CI", null)).Error);
        await tokens.RevokeAsync(user.Id, first!.Info.Id);
        Assert.Null((await tokens.CreateAsync(user.Id, "ci", null)).Error);
    }

    [Fact]
    public async Task Each_user_is_capped_at_twenty_active_tokens()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("pat");
        var tokens = db.Tokens();
        for (var i = 0; i < ApiTokenService.MaxActiveTokensPerUser; i++)
            Assert.Null((await tokens.CreateAsync(user.Id, "t" + i, null)).Error);
        Assert.NotNull((await tokens.CreateAsync(user.Id, "one too many", null)).Error);

        var oldest = (await tokens.ListAsync(user.Id)).Last();
        await tokens.RevokeAsync(user.Id, oldest.Id);
        Assert.Null((await tokens.CreateAsync(user.Id, "now fits", null)).Error);
    }

    [Fact]
    public async Task List_is_per_user_newest_first()
    {
        await using var db = await TempDb.CreateAsync();
        var a = await db.UserAsync("quinn");
        var b = await db.UserAsync("rita");
        var tokens = db.Tokens();
        await tokens.CreateAsync(a.Id, "first", null);
        await tokens.CreateAsync(b.Id, "other", null);
        await tokens.CreateAsync(a.Id, "second", null);

        Assert.Equal(new[] { "second", "first" }, (await tokens.ListAsync(a.Id)).Select(t => t.Name));
        Assert.Equal(new[] { "other" }, (await tokens.ListAsync(b.Id)).Select(t => t.Name));
    }

    [Fact]
    public async Task Schema_is_created_on_an_existing_database_that_predates_tokens()
    {
        await using var db = await TempDb.CreateAsync();
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='ApiTokens'"));

        var user = await db.UserAsync("sam");
        await db.Tokens().ListAsync(user.Id);

        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ApiTokens'"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ApiTokens_TokenHash'"));
        // Existing tables and rows are untouched.
        Assert.True(await db.ScalarAsync("SELECT COUNT(*) FROM Users") >= 2);
    }

    [Fact]
    public async Task An_older_tokens_table_missing_columns_is_upgraded_in_place_keeping_its_rows()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("tess");
        await db.ExecAsync("""
            CREATE TABLE "ApiTokens" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "UserId" INTEGER NOT NULL, "Name" TEXT NOT NULL,
                                      "Prefix" TEXT NOT NULL, "TokenHash" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL)
            """);
        var legacyToken = ApiTokenFormat.Generate();
        await db.ExecAsync($"""
            INSERT INTO "ApiTokens" ("UserId","Name","Prefix","TokenHash","CreatedAt")
            VALUES ({user.Id}, 'legacy', '{ApiTokenFormat.DisplayPrefix(legacyToken)}', '{ApiTokenFormat.Hash(legacyToken)}', '2026-01-01T00:00:00.0000000+00:00')
            """);

        var tokens = db.Tokens();
        var listed = (await tokens.ListAsync(user.Id)).Single();
        Assert.Equal("legacy", listed.Name);
        Assert.Equal("read", listed.Scopes); // the added column's default
        Assert.Null(listed.ExpiresAt);
        Assert.True((await tokens.ValidateAsync(legacyToken)).IsValid);
    }

    [Fact]
    public async Task Schema_bootstrap_is_idempotent_across_service_instances()
    {
        await using var db = await TempDb.CreateAsync();
        var user = await db.UserAsync("uma");
        for (var i = 0; i < 3; i++) await db.Tokens().CreateAsync(user.Id, "n" + i, null);
        Assert.Equal(3, (await db.Tokens().ListAsync(user.Id)).Count);
    }

    [Fact]
    public async Task Active_state_helpers_agree_with_the_stored_fields()
    {
        var now = DateTimeOffset.UtcNow;
        var active = new ApiTokenInfo(1, 1, "a", "aip_x", "read", now, now.AddDays(1), null, null);
        var expired = active with { ExpiresAt = now };
        var revoked = active with { RevokedAt = now };
        Assert.True(active.IsActive(now));
        Assert.False(expired.IsActive(now));
        Assert.False(revoked.IsActive(now));
        Assert.Equal(new[] { "read" }, active.ScopeList);
        await Task.CompletedTask;
    }
}
