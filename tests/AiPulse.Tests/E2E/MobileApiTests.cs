using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Security;
using Xunit;

namespace AiPulse.Tests.E2E;

/// <summary>The JSON API the Android/iOS app talks to (/api/mobile/v1): sign-in, bearer auth, and that it coexists with the public /api/v1.</summary>
[Collection(E2ECollection.Name)]
public class MobileApiTests(AiPulseAppFactory app)
{
    private const string Login = "/api/mobile/v1/auth/login";

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private static int _nextClient;

    /// <summary>
    /// Each client claims its own address through X-Forwarded-For (the rate limiter keys on the last hop, as it does behind nginx), so the
    /// per-IP sign-in limit - which does apply to the app's login - never trips over the many sign-ins the other end-to-end tests make.
    /// </summary>
    private HttpClient Fresh()
    {
        var client = app.Anonymous();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.77.{Interlocked.Increment(ref _nextClient) % 250}.{Environment.CurrentManagedThreadId % 250 + 1}");
        return client;
    }

    private async Task<string> TokenAsync(string user, string password)
    {
        var response = await Fresh().PostAsync(Login, Json(new { username = user, password }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private HttpClient Bearer(string token)
    {
        var client = Fresh();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task A_valid_login_returns_a_token_that_opens_the_news_feed()
    {
        var token = await TokenAsync(AiPulseAppFactory.ReaderUser, app.ReaderPassword);
        var feed = await Bearer(token).GetAsync("/api/mobile/v1/news?pageSize=5");

        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        using var doc = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task A_wrong_password_is_a_plain_401_not_a_redirect()
    {
        var response = await Fresh().PostAsync(Login, Json(new { username = AiPulseAppFactory.ReaderUser, password = "wrong-password" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Private_endpoints_answer_401_without_a_token_instead_of_redirecting_to_the_login_page()
    {
        var response = await Fresh().GetAsync("/api/mobile/v1/bookmarks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_forged_token_is_refused()
    {
        var response = await Bearer("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJuYW1lIjoiYWRtaW4ifQ.AAAA").GetAsync("/api/mobile/v1/bookmarks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_old_placeholder_secret_cannot_mint_tokens()
    {
        // The repository once shipped a known Jwt:Secret; a token signed with it must not be accepted by any deployment.
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("CHANGE-ME-dev-only-secret-at-least-32-characters-long"));
        var forged = handler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "AiPulse", audience: "AiPulse",
            claims: new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin") },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)));

        var response = await Bearer(forged).GetAsync("/api/mobile/v1/bookmarks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_stops_working_when_the_account_signs_out_everywhere()
    {
        var users = app.Services.GetRequiredService<UserService>();
        await users.CreateApprovedUserAsync("mobile-stamp", "Mobile-Stamp-1!", UserRoles.User);
        var token = await TokenAsync("mobile-stamp", "Mobile-Stamp-1!");
        Assert.Equal(HttpStatusCode.OK, (await Bearer(token).GetAsync("/api/mobile/v1/bookmarks")).StatusCode);

        var user = (await users.GetByUsernameAsync("mobile-stamp"))!;
        await users.BumpSecurityStampAsync(user.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(token).GetAsync("/api/mobile/v1/bookmarks")).StatusCode);
    }

    [Fact]
    public async Task Two_step_sign_in_applies_to_the_app_too()
    {
        var users = app.Services.GetRequiredService<UserService>();
        var twoFactor = app.Services.GetRequiredService<TwoFactorService>();
        await users.CreateApprovedUserAsync("mobile-2fa", "Mobile-2fa-1!", UserRoles.User);
        var user = (await users.GetByUsernameAsync("mobile-2fa"))!;
        var enrollment = await twoFactor.BeginEnrollmentAsync(user.Id);
        var secret = Base32.Decode(enrollment.ManualKey);
        var confirmed = await twoFactor.ConfirmEnrollmentAsync(user.Id, Totp.CodeAt(secret, DateTimeOffset.UtcNow));
        Assert.True(confirmed.Ok, confirmed.Error);

        var client = Fresh();
        var noCode = await client.PostAsync(Login, Json(new { username = "mobile-2fa", password = "Mobile-2fa-1!" }));
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("twoFactorRequired", await noCode.Content.ReadAsStringAsync());

        var wrong = await client.PostAsync(Login, Json(new { username = "mobile-2fa", password = "Mobile-2fa-1!", code = "000000" }));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // The enrolment code already used this step; the next step's code is within the accepted window and is not a replay.
        var good = await client.PostAsync(Login, Json(new { username = "mobile-2fa", password = "Mobile-2fa-1!", code = Totp.CodeAt(secret, DateTimeOffset.UtcNow.AddSeconds(Totp.PeriodSeconds)) }));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task The_mobile_and_public_APIs_live_side_by_side_without_a_route_clash()
    {
        var publicNews = await app.Anonymous().GetAsync("/api/v1/news?pageSize=3");
        var mobileNews = await Bearer(await TokenAsync(AiPulseAppFactory.ReaderUser, app.ReaderPassword)).GetAsync("/api/mobile/v1/news?pageSize=3");

        Assert.Equal(HttpStatusCode.OK, publicNews.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mobileNews.StatusCode);
        app.AssertNoServerErrors();
    }
}

public class JwtSigningKeyTests
{
    [Fact]
    public void A_real_configured_secret_is_used_as_is()
    {
        var secret = new string('s', 40);
        var key = JwtSigningKey.Resolve(secret, Path.Combine(Path.GetTempPath(), "never-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(secret), key.Bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("CHANGE-ME-dev-only-secret-at-least-32-characters-long")]
    public void Missing_short_or_placeholder_secrets_are_replaced_by_a_generated_key_that_is_kept(string? configured)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jwtkey-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "jwt.key");
        try
        {
            var first = JwtSigningKey.Resolve(configured, file);
            var second = JwtSigningKey.Resolve(configured, file);

            Assert.True(first.Bytes.Length >= JwtSigningKey.MinLength);
            Assert.True(File.Exists(file));
            Assert.Equal(first.Bytes, second.Bytes);   // stable across restarts, so phones stay signed in
            Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes("CHANGE-ME-dev-only-secret-at-least-32-characters-long"), first.Bytes);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Two_installs_get_different_keys()
    {
        var a = Path.Combine(Path.GetTempPath(), "jwtkey-" + Guid.NewGuid().ToString("N"), "jwt.key");
        var b = Path.Combine(Path.GetTempPath(), "jwtkey-" + Guid.NewGuid().ToString("N"), "jwt.key");
        try { Assert.NotEqual(JwtSigningKey.Resolve(null, a).Bytes, JwtSigningKey.Resolve(null, b).Bytes); }
        finally { foreach (var f in new[] { a, b }) try { Directory.Delete(Path.GetDirectoryName(f)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_corrupt_key_file_is_replaced_not_trusted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jwtkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "jwt.key");
        try
        {
            File.WriteAllText(file, "not base64 !!!");
            var key = JwtSigningKey.Resolve(null, file);
            Assert.True(key.Bytes.Length >= JwtSigningKey.MinLength);
            Assert.Equal(key.Bytes, JwtSigningKey.Resolve(null, file).Bytes);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
