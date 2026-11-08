using System.Net;
using System.Text;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiPulse.Tests;

/// <summary>
/// The security layer wired into a real (in-process) ASP.NET Core pipeline via the same AddSecurityHardening call Program.cs
/// makes. "/login" here stands in for Login.razor: it checks the password and calls SignInAsync, then redirects the way Blazor's
/// NavigationManager does (an absolute URL) - the guard has to turn that into the 2FA step without the page knowing.
/// </summary>
internal sealed class SecurityHost : IDisposable
{
    public readonly SecurityRig Rig = new();
    public readonly TestServer Server;
    private readonly Dictionary<string, string> _cookies = new();

    public SecurityHost(Action<SecurityHardeningOptions>? configure = null, string baseAddress = "http://localhost/")
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s =>
            {
                s.AddLogging();
                s.AddRouting();
                s.AddSingleton(Rig.Db);
                s.AddSingleton(Rig.Protection);
                s.AddSingleton(Rig.Users);
                s.AddSingleton(Rig.TwoFactor);
                s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => o.Cookie.Name = "AiPulse.Auth");
                s.AddSecurityHardening();
                if (configure is not null) s.Configure(configure);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseEndpoints(e =>
                {
                    e.MapPost("/login", async (HttpContext http, UserService users) =>
                    {
                        var form = await http.Request.ReadFormAsync();
                        var (result, user) = await users.TryLoginAsync(form["username"], form["password"]);
                        if (result != LoginResult.Success || user is null) return Results.Text("bad", statusCode: 401);
                        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, TwoFactorLoginFlow.BuildPrincipal(user, mfa: false),
                            new AuthenticationProperties { IsPersistent = form["remember"] == "true" });
                        var target = form["redirect"].FirstOrDefault() ?? "http://localhost/dashboard?tab=news";
                        if (form["enhanced"] == "true")
                        {
                            http.Response.Headers["blazor-enhanced-nav-redirect-location"] = target;
                            return Results.Ok();
                        }
                        return Results.Redirect(target);
                    });
                    e.MapPost("/login/2fa", async (HttpContext http, TwoFactorLoginFlow flow) =>
                    {
                        var form = await http.Request.ReadFormAsync();
                        var r = await flow.CompleteAsync(http, form["code"]);
                        return r.Status == TwoFactorCompletionStatus.Success ? Results.Redirect(r.RedirectTo!) : Results.Text(r.Status.ToString());
                    });
                    e.MapPost("/auth/clerk/session", async (HttpContext http, UserService users) =>
                    {
                        var user = await users.GetByUsernameAsync((await http.Request.ReadFormAsync())["username"]!);
                        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, TwoFactorLoginFlow.BuildPrincipal(user!, mfa: false));
                        return Results.Ok();
                    });
                    e.MapGet("/whoami", (HttpContext http) => http.User.Identity?.IsAuthenticated == true
                        ? $"{http.User.Identity.Name}|{http.User.FindFirst(TwoFactorLoginFlow.MfaClaimType)?.Value}" : "anonymous");
                    e.MapGet("/af-token", (HttpContext http, Microsoft.AspNetCore.Antiforgery.IAntiforgery af) => af.GetAndStoreTokens(http).RequestToken!);
                    e.MapGet("/page", () => Results.Content("<!doctype html><title>t</title>", "text/html; charset=utf-8"));
                    e.MapGet("/data", () => Results.Json(new { ok = true }));
                });
            });
        Server = new TestServer(builder) { BaseAddress = new Uri(baseAddress) };
    }

    public bool HasCookie(string name) => _cookies.ContainsKey(name);
    public void SetCookie(string name, string value) => _cookies[name] = value;
    public void ForgetCookies() => _cookies.Clear();

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form = null, Action<HttpRequestMessage>? tweak = null)
    {
        var client = Server.CreateClient();
        var request = new HttpRequestMessage(method, path);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        if (_cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        tweak?.Invoke(request);
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            foreach (var header in setCookies)
            {
                var pair = header.Split(';')[0];
                var eq = pair.IndexOf('=');
                var name = pair[..eq];
                var value = pair[(eq + 1)..];
                var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) || value.Length == 0;
                if (expired) _cookies.Remove(name); else _cookies[name] = value;
            }
        return response;
    }

    public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form, Action<HttpRequestMessage>? tweak = null) =>
        SendAsync(HttpMethod.Post, path, form, tweak);

    public async Task<string> WhoAmIAsync() => await (await SendAsync(HttpMethod.Get, "/whoami")).Content.ReadAsStringAsync();

    public Task<HttpResponseMessage> LoginAsync(string user, string password, Dictionary<string, string>? extra = null, string path = "/login")
    {
        var form = new Dictionary<string, string> { ["username"] = user, ["password"] = password };
        foreach (var kv in extra ?? new()) form[kv.Key] = kv.Value;
        return PostAsync(path, form);
    }

    public void Dispose()
    {
        Server.Dispose();
        Rig.Dispose();
    }
}

public class TwoFactorSignInTests
{
    [Fact]
    public async Task Accounts_without_2fa_sign_in_with_just_the_password()
    {
        using var host = new SecurityHost();
        await host.Rig.UserAsync();

        var response = await host.LoginAsync("alice", "alice-pass-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("http://localhost/dashboard?tab=news", response.Headers.Location!.ToString());
        Assert.True(host.HasCookie("AiPulse.Auth"));
        Assert.Equal("alice|", await host.WhoAmIAsync());
    }

    [Fact]
    public async Task With_2fa_the_password_step_issues_no_session_only_a_short_lived_challenge()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        await host.Rig.EnrolAsync(alice.Id);

        var response = await host.LoginAsync("alice", "alice-pass-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login/2fa", response.Headers.Location!.ToString());
        Assert.False(host.HasCookie("AiPulse.Auth"));
        Assert.True(host.HasCookie(TwoFactorLoginFlow.ChallengeCookieName));
        var challengeHeader = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(TwoFactorLoginFlow.ChallengeCookieName));
        Assert.Contains("path=/login/2fa", challengeHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", challengeHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", challengeHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", challengeHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("anonymous", await host.WhoAmIAsync());
        Assert.Contains("Login.TwoFactorRequired", await host.Rig.AuditActionsAsync());
    }

    [Fact]
    public async Task A_valid_code_completes_sign_in_with_an_mfa_claim_and_returns_to_where_the_page_was_going()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1");

        var wrong = await host.PostAsync("/login/2fa", new() { ["code"] = "000000" });
        Assert.Equal("Invalid", await wrong.Content.ReadAsStringAsync());
        Assert.False(host.HasCookie("AiPulse.Auth"));

        var right = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Equal("/dashboard?tab=news", right.Headers.Location!.ToString());
        Assert.True(host.HasCookie("AiPulse.Auth"));
        Assert.False(host.HasCookie(TwoFactorLoginFlow.ChallengeCookieName));
        Assert.Equal("alice|mfa", await host.WhoAmIAsync());
    }

    [Fact]
    public async Task Keep_me_signed_in_survives_the_second_step()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1", new() { ["remember"] = "true" });

        var right = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        var auth = right.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("AiPulse.Auth"));
        Assert.Contains("expires=", auth, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recovery_code_also_completes_sign_in()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (_, codes) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1");

        var response = await host.PostAsync("/login/2fa", new() { ["code"] = codes[0] });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("alice|mfa", await host.WhoAmIAsync());
    }

    [Fact]
    public async Task Without_a_challenge_the_code_step_does_nothing()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);

        var response = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        Assert.Equal("Expired", await response.Content.ReadAsStringAsync());
        Assert.False(host.HasCookie("AiPulse.Auth"));
    }

    [Fact]
    public async Task A_tampered_challenge_is_rejected()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        host.SetCookie(TwoFactorLoginFlow.ChallengeCookieName, "CfDJ8forged");

        var response = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        Assert.Equal("Expired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_password_reset_between_the_two_steps_voids_the_challenge()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1");
        await host.Rig.Users.SetPasswordAsync(alice.Id, "brand-new-pass"); // rotates the security stamp

        var response = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        Assert.Equal("Expired", await response.Content.ReadAsStringAsync());
        Assert.False(host.HasCookie("AiPulse.Auth"));
    }

    [Fact]
    public async Task Blazor_enhanced_navigation_redirects_are_rewritten_too()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        await host.Rig.EnrolAsync(alice.Id);

        var response = await host.LoginAsync("alice", "alice-pass-1", new() { ["enhanced"] = "true" });

        Assert.Equal("/login/2fa", response.Headers.GetValues("blazor-enhanced-nav-redirect-location").Single());
        Assert.False(host.HasCookie("AiPulse.Auth"));
        Assert.True(host.HasCookie(TwoFactorLoginFlow.ChallengeCookieName));
    }

    [Fact]
    public async Task An_off_site_redirect_never_becomes_the_return_url()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1", new() { ["redirect"] = "https://evil.example/steal" }, path: "/login?ReturnUrl=%2Fbookmarks");

        var response = await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) });

        Assert.Equal("/bookmarks", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task External_provider_sign_ins_are_not_asked_for_a_code()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        await host.Rig.EnrolAsync(alice.Id);

        await host.PostAsync("/auth/clerk/session", new() { ["username"] = "alice" });

        Assert.Equal("alice|", await host.WhoAmIAsync());
    }

    [Fact]
    public async Task Wrong_codes_at_sign_in_lock_the_account_after_five()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        var (secret, _) = await host.Rig.EnrolAsync(alice.Id);
        await host.LoginAsync("alice", "alice-pass-1");

        for (var i = 0; i < 4; i++)
            Assert.Equal("Invalid", await (await host.PostAsync("/login/2fa", new() { ["code"] = "000000" })).Content.ReadAsStringAsync());
        Assert.Equal("LockedOut", await (await host.PostAsync("/login/2fa", new() { ["code"] = "000000" })).Content.ReadAsStringAsync());
        Assert.Equal("LockedOut", await (await host.PostAsync("/login/2fa", new() { ["code"] = host.Rig.CurrentCode(secret) })).Content.ReadAsStringAsync());
        Assert.False(host.HasCookie("AiPulse.Auth"));
    }

    [Theory]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/news?tag=agents", "/news?tag=agents")]
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("https://evil.example/", null)]
    [InlineData("dashboard", null)]
    [InlineData("/a\r\nSet-Cookie: x", null)]
    [InlineData("", null)]
    public void Local_urls_are_the_only_ones_kept(string input, string? expected) => Assert.Equal(expected, LocalUrl.Sanitize(input));
}

public class SignOutEverywhereTests
{
    [Fact]
    public async Task Signing_out_everywhere_needs_a_session_and_a_valid_antiforgery_token()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();

        var anonymous = await host.PostAsync("/account/sign-out-everywhere", new());
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/login", anonymous.Headers.Location!.ToString());

        await host.LoginAsync("alice", "alice-pass-1");
        var forged = await host.PostAsync("/account/sign-out-everywhere", new() { ["__RequestVerificationToken"] = "forged" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("alice|", await host.WhoAmIAsync());
        Assert.Equal(alice.SecurityStamp, (await host.Rig.Users.GetByIdAsync(alice.Id))!.SecurityStamp);
    }

    [Fact]
    public async Task With_a_valid_token_every_session_is_revoked_and_this_cookie_cleared()
    {
        using var host = new SecurityHost();
        var alice = await host.Rig.UserAsync();
        await host.LoginAsync("alice", "alice-pass-1");
        var token = await (await host.SendAsync(HttpMethod.Get, "/af-token")).Content.ReadAsStringAsync();

        var response = await host.PostAsync("/account/sign-out-everywhere", new() { ["__RequestVerificationToken"] = token });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.ToString());
        Assert.False(host.HasCookie("AiPulse.Auth"));
        Assert.NotEqual(alice.SecurityStamp, (await host.Rig.Users.GetByIdAsync(alice.Id))!.SecurityStamp);
        Assert.Contains("Session.SignOutEverywhere", await host.Rig.AuditActionsAsync());
    }
}

public class SecurityHeaderTests
{
    [Fact]
    public async Task Html_pages_get_the_baseline_headers_a_report_only_csp_and_enforced_frame_ancestors()
    {
        using var host = new SecurityHost();
        var response = await host.SendAsync(HttpMethod.Get, "/page");

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeaderPolicy.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Equal("frame-ancestors 'self'", Header(response, "Content-Security-Policy"));
        var csp = Header(response, "Content-Security-Policy-Report-Only")!;
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("connect-src 'self' ws://localhost", csp);
        Assert.Contains("frame-src 'self' https: http:", csp); // Dashboard widgets may embed any site
        Assert.Contains("img-src 'self' data: blob: https: http:", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("report-uri /csp-report", csp);
        Assert.DoesNotContain("clerk", csp);
        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Non_html_responses_get_the_baseline_but_no_page_policy()
    {
        using var host = new SecurityHost();
        var response = await host.SendAsync(HttpMethod.Get, "/data");

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Null(Header(response, "Content-Security-Policy-Report-Only"));
        Assert.Equal("frame-ancestors 'self'", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Hsts_is_sent_over_https_but_not_for_localhost_or_plain_http()
    {
        using (var https = new SecurityHost(baseAddress: "https://aipulse.example/"))
        {
            var response = await https.SendAsync(HttpMethod.Get, "/page");
            Assert.Equal("max-age=15552000", Header(response, "Strict-Transport-Security"));
            Assert.Contains("connect-src 'self' wss://aipulse.example", Header(response, "Content-Security-Policy-Report-Only"));
        }
        using (var local = new SecurityHost(baseAddress: "https://localhost/"))
            Assert.Null(Header(await local.SendAsync(HttpMethod.Get, "/page"), "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Extra_frame_ancestors_replace_x_frame_options_and_unsafe_entries_are_dropped()
    {
        using var host = new SecurityHost(o => o.FrameAncestors = new[] { "https://intranet.example", "https://x.example; script-src *" });
        var response = await host.SendAsync(HttpMethod.Get, "/page");

        Assert.Null(Header(response, "X-Frame-Options"));
        Assert.Equal("frame-ancestors 'self' https://intranet.example", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Turning_off_report_only_enforces_the_whole_policy()
    {
        using var host = new SecurityHost(o => o.CspReportOnly = false);
        var response = await host.SendAsync(HttpMethod.Get, "/page");

        Assert.Null(Header(response, "Content-Security-Policy-Report-Only"));
        Assert.Contains("script-src 'self'", Header(response, "Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'self'", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public void The_policy_allows_the_clerk_frontend_api_only_when_clerk_is_on()
    {
        var csp = SecurityHeaderPolicy.ContentSecurityPolicy("aipulse.example", true, "clerk.aipulse.example", Array.Empty<string>());
        Assert.Contains("script-src 'self' 'unsafe-inline' https://clerk.aipulse.example https://*.clerk.accounts.dev https://*.clerk.com", csp);
        Assert.Contains("connect-src 'self' wss://aipulse.example https://clerk.aipulse.example", csp);
    }

    [Fact]
    public async Task Csp_reports_in_both_formats_are_logged_and_answered_with_204()
    {
        using var host = new SecurityHost();
        var log = host.Server.Services.GetRequiredService<CspViolationLog>();

        var classic = await host.SendAsync(HttpMethod.Post, "/csp-report", tweak: r => r.Content = new StringContent(
            """{"csp-report":{"document-uri":"http://localhost/news","violated-directive":"script-src-elem","blocked-uri":"https://cdn.example/x.js"}}""",
            Encoding.UTF8, "application/csp-report"));
        var modern = await host.SendAsync(HttpMethod.Post, "/csp-report", tweak: r => r.Content = new StringContent(
            """[{"type":"csp-violation","body":{"documentURL":"http://localhost/","effectiveDirective":"img-src","blockedURL":"ftp://x\n\rforged"}}]""",
            Encoding.UTF8, "application/reports+json"));
        var junk = await host.SendAsync(HttpMethod.Post, "/csp-report", tweak: r => r.Content = new StringContent("not json"));
        var get = await host.SendAsync(HttpMethod.Get, "/csp-report");

        Assert.Equal(HttpStatusCode.NoContent, classic.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, modern.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, junk.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        var recent = log.Recent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("img-src", recent[0].Directive);
        Assert.DoesNotContain('\n', recent[0].BlockedUri);
        Assert.Equal("https://cdn.example/x.js", recent[1].BlockedUri);
    }

    [Fact]
    public void The_violation_log_keeps_only_the_latest_hundred()
    {
        var log = new CspViolationLog();
        for (var i = 0; i < 150; i++) log.Add(new CspViolation(DateTimeOffset.UtcNow, "d", i.ToString(), "u", null));
        Assert.Equal(100, log.Recent().Count);
        Assert.Equal("149", log.Recent()[0].BlockedUri);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : response.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;
}

public class SecurityRateLimitTests
{
    [Fact]
    public async Task Sign_in_posts_are_limited_per_client_and_answered_with_429()
    {
        using var host = new SecurityHost();
        for (var i = 0; i < SecurityRateLimits.Login.PermitLimit; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await host.LoginAsync("nobody", "x")).StatusCode);

        var limited = await host.LoginAsync("nobody", "x");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());

        // Another client (as the proxy reports it) is unaffected, and so are ordinary pages.
        var other = await host.PostAsync("/login", new() { ["username"] = "nobody", ["password"] = "x" }, r => r.Headers.Add("X-Forwarded-For", "203.0.113.9"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/page")).StatusCode);
    }

    [Fact]
    public async Task The_limit_can_be_switched_off()
    {
        using var host = new SecurityHost(o => o.RateLimitingEnabled = false);
        for (var i = 0; i < SecurityRateLimits.Login.PermitLimit + 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await host.LoginAsync("nobody", "x")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/login", "login")]
    [InlineData("GET", "/login", null)]
    [InlineData("POST", "/login/2fa", "two-factor")]
    [InlineData("POST", "/auth/clerk/session", "clerk-session")]
    [InlineData("GET", "/auth/external/google", "external-signin")]
    [InlineData("GET", "/register", "account-pages")]
    [InlineData("GET", "/forgot-password", "account-pages")]
    [InlineData("GET", "/reset-password", "account-pages")]
    [InlineData("POST", "/account/sign-out-everywhere", "sign-out-everywhere")]
    [InlineData("POST", "/csp-report", "csp-report")]
    [InlineData("GET", "/news", null)]
    [InlineData("POST", "/_blazor", null)]
    public void Rules_cover_the_anonymous_auth_endpoints(string method, string path, string? rule) =>
        Assert.Equal(rule, SecurityRateLimits.Match(method, path)?.Name);

    [Fact]
    public void The_client_key_is_the_last_forwarded_hop_like_program_cs_reads_it()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.2");
        Assert.Equal("10.0.0.2", SecurityRateLimits.ClientKey(context));
        context.Request.Headers["X-Forwarded-For"] = "1.1.1.1, 198.51.100.7";
        Assert.Equal("198.51.100.7", SecurityRateLimits.ClientKey(context));
    }
}

public class LoginHardeningTests
{
    [Fact]
    public async Task Unknown_usernames_get_the_same_answer_as_wrong_passwords()
    {
        using var rig = new SecurityRig();
        await rig.UserAsync();
        Assert.Equal(LoginResult.InvalidCredentials, (await rig.Users.TryLoginAsync("no-such-user", "whatever")).Result);
        Assert.Equal(LoginResult.InvalidCredentials, (await rig.Users.TryLoginAsync("alice", "wrong")).Result);
    }

    [Fact]
    public async Task Reset_links_are_stored_hashed_single_use_and_using_one_retires_the_others()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var first = await rig.Users.GenerateResetLinkForAdminAsync(alice.Id, t => t);
        var second = await rig.Users.GenerateResetLinkForAdminAsync(alice.Id, t => t);

        await using (var db = await rig.Db.CreateDbContextAsync())
            Assert.DoesNotContain(db.PasswordResetTokens.ToList(), t => t.TokenHash == first || t.TokenHash == second);

        Assert.True((await rig.Users.ResetPasswordWithTokenAsync(first, "new-pass-1")).Success);
        Assert.False((await rig.Users.ResetPasswordWithTokenAsync(first, "new-pass-2")).Success);
        Assert.False((await rig.Users.ResetPasswordWithTokenAsync(second, "new-pass-3")).Success);
        Assert.Equal(LoginResult.Success, (await rig.Users.TryLoginAsync("alice", "new-pass-1")).Result);
    }

    [Fact]
    public async Task Reset_requests_are_throttled_per_address_without_saying_so()
    {
        using var rig = new SecurityRig();
        await rig.Users.RegisterAsync("carol", "carol-pass-1", "carol@example.test");

        for (var i = 0; i < 5; i++)
            await rig.Users.RequestPasswordResetAsync("Carol@Example.test ", t => t);

        Assert.Equal(3, rig.Email.Sent.Count);
        await rig.Users.RequestPasswordResetAsync("nobody@example.test", t => t); // unknown address: same silent result
        Assert.Equal(3, rig.Email.Sent.Count);
    }

    [Fact]
    public async Task Registrations_are_capped_over_a_short_window()
    {
        using var rig = new SecurityRig();
        for (var i = 0; i < 20; i++)
            Assert.True((await rig.Users.RegisterAsync($"user{i:00}", "pass-123456")).Success);

        var (ok, error) = await rig.Users.RegisterAsync("user99", "pass-123456");
        Assert.False(ok);
        Assert.Contains("Too many sign-ups", error);
    }

    [Fact]
    public async Task Changing_your_own_password_is_audited_and_retires_reset_links()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var link = await rig.Users.GenerateResetLinkForAdminAsync(alice.Id, t => t);

        Assert.False((await rig.Users.ChangeOwnPasswordAsync("alice", "wrong", "new-pass-1")).Success);
        Assert.True((await rig.Users.ChangeOwnPasswordAsync("alice", "alice-pass-1", "new-pass-1")).Success);

        Assert.False((await rig.Users.ResetPasswordWithTokenAsync(link, "another-1")).Success);
        var audit = await rig.AuditActionsAsync();
        Assert.Contains("User.PasswordChangeFailed", audit);
        Assert.Contains("User.PasswordChanged", audit);
    }
}
