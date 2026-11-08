using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests.E2E;

/// <summary>The machine-facing endpoints, sign-in plumbing, static assets and headers of the real app.</summary>
[Collection(E2ECollection.Name)]
public class EndpointTests(AiPulseAppFactory app)
{
    [Fact]
    public async Task Robots_txt_allows_the_public_pages_and_points_at_the_sitemap()
    {
        var response = await app.Anonymous().GetAsync("/robots.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("User-agent: *", body);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", body);
        foreach (var page in PublicSite.Menu) Assert.Contains($"Allow: {page.Path}\n", body);
        Assert.Contains("Disallow: /\n", body);
    }

    [Fact]
    public async Task Sitemap_lists_every_public_page_and_busy_topics()
    {
        var response = await app.Anonymous().GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locs = doc.Descendants(ns + "loc").Select(e => e.Value).ToList();
        Assert.Contains("http://localhost/", locs);
        foreach (var page in PublicSite.Menu.Concat(PublicSite.Entry)) Assert.Contains("http://localhost" + page.Path, locs);
        Assert.Contains(locs, l => l.StartsWith("http://localhost/topics/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Atom_feed_carries_the_latest_headlines_and_filters_by_tag()
    {
        var client = app.Anonymous();
        var response = await client.GetAsync("/feed.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/atom+xml", response.Content.Headers.ContentType?.MediaType);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var feed = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var titles = feed.Descendants(atom + "entry").Select(e => (string?)e.Element(atom + "title")).ToList();
        Assert.Contains(titles, t => t is not null && t.StartsWith("E2E headline", StringComparison.Ordinal));

        var tagged = XDocument.Parse(await client.GetStringAsync("/feed.xml?tag=" + AiPulseAppFactory.SeededTag));
        Assert.Contains(AiPulseAppFactory.SeededTag, (string?)tagged.Root!.Element(atom + "title") ?? "");
        Assert.NotEmpty(tagged.Descendants(atom + "entry"));
    }

    [Fact]
    public async Task Web_app_manifest_service_worker_and_offline_page_are_served()
    {
        var client = app.Anonymous();

        var manifest = await client.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        using (var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync()))
        {
            Assert.Equal("AiPulse", json.RootElement.GetProperty("name").GetString());
            Assert.True(json.RootElement.GetProperty("icons").GetArrayLength() > 0);
        }

        var sw = await client.GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Contains("javascript", sw.Content.Headers.ContentType?.MediaType);
        Assert.Contains("/offline.html", await sw.Content.ReadAsStringAsync());

        var offline = await client.GetAsync("/offline.html");
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.Equal("text/html", offline.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>", await offline.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/app.css", "text/css")]
    [InlineData("/favicon.png", "image/png")]
    [InlineData("/js/notify.js", "javascript")]
    [InlineData("/AiPulse.styles.css", "text/css")]
    public async Task Static_assets_are_served_and_always_revalidated(string path, string contentType)
    {
        var response = await app.Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(contentType, response.Content.Headers.ContentType?.MediaType);
        // Program.cs sends no-cache so an updated app.css/notify.js is never served stale from the browser cache.
        Assert.True(response.Headers.CacheControl?.NoCache, $"{path} should be sent with Cache-Control: no-cache");
        Assert.NotNull(response.Headers.ETag ?? (object?)response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task Blazor_script_is_served()
    {
        var response = await app.Anonymous().GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy_without_signing_in()
    {
        var response = await app.Anonymous().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_form_rejects_a_post_without_its_antiforgery_token()
    {
        var client = app.Anonymous();
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["Input.Username"] = AiPulseAppFactory.AdminUser,
            ["Input.Password"] = app.AdminPassword,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith("AiPulse.Auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_with_a_wrong_password_stays_on_the_form()
    {
        var client = app.Anonymous();
        var response = await AiPulseAppFactory.PostLoginAsync(client, AiPulseAppFactory.ReaderUser, "not-the-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid username or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_issues_a_locked_down_cookie_and_honours_a_local_return_url()
    {
        var client = app.Anonymous();
        var token = await AiPulseAppFactory.LoginTokenAsync(client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = AiPulseAppFactory.ReaderUser,
            ["Input.Password"] = app.ReaderPassword,
        });
        var response = await client.PostAsync("/login?ReturnUrl=%2Fsettings", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/settings", response.Headers.Location!.ToString());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("AiPulse.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings")).StatusCode);

        // Signing out ends the session.
        var logout = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task Html_pages_are_utf8_and_the_login_form_cannot_be_framed()
    {
        var response = await app.Anonymous().GetAsync("/login");
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.Contains("X-Frame-Options"), "The login page must not be frameable (clickjacking).");
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Fact]
    public async Task Clerk_session_endpoint_refuses_sign_in_while_clerk_is_disabled()
    {
        var client = app.Anonymous();
        var response = await client.PostAsJsonAsync("/auth/clerk/session", new { token = "not-a-real-token", remember = false, returnUrl = "/dashboard" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Clerk sign-in is not enabled.", json.RootElement.GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(app.Outbound.Blocked.Where(u => u.Host.Contains("clerk", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Clerk_session_endpoint_only_accepts_json_so_a_cross_site_form_cannot_use_it()
    {
        var response = await app.Anonymous().PostAsync("/auth/clerk/session",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task External_sign_in_is_refused_for_unknown_or_unconfigured_providers()
    {
        var client = app.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/external/myspace")).StatusCode);
        var google = await client.GetAsync("/auth/external/google");
        Assert.Equal(HttpStatusCode.Redirect, google.StatusCode);
        Assert.EndsWith("/login", google.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Whole_site_backup_download_is_admin_only()
    {
        var anonymous = await app.Anonymous().GetAsync("/backup/download");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/login", anonymous.Headers.Location!.ToString());

        // The zip holds every user's password hash and the data-protection keys: a reader must never get it.
        var reader = await (await app.SignedInAs(AiPulseAppFactory.ReaderUser)).GetAsync("/backup/download");
        Assert.Equal(HttpStatusCode.Redirect, reader.StatusCode);
        Assert.Contains("/access-denied", reader.Headers.Location!.ToString());

        var admin = await (await app.SignedInAs(AiPulseAppFactory.AdminUser)).GetAsync("/backup/download");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal("application/zip", admin.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await admin.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName == "aipulse.db");
    }

    [Fact]
    public async Task Admin_only_file_endpoints_refuse_a_reader()
    {
        var reader = await app.SignedInAs(AiPulseAppFactory.ReaderUser);
        foreach (var path in new[] { "/opml/export" })
        {
            var response = await reader.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/access-denied", response.Headers.Location!.ToString());
        }

        var restore = await reader.PostAsync("/backup/restore", new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "file", "x.zip" } });
        Assert.NotEqual(HttpStatusCode.OK, restore.StatusCode);
        Assert.Contains("/access-denied", restore.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task Fever_api_answers_unauthenticated_clients_without_data()
    {
        var response = await app.Anonymous().GetAsync("/fever/?api");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("auth").GetInt32());
    }

    [Fact]
    public async Task Favicon_proxy_is_not_an_open_proxy()
    {
        var client = app.Anonymous();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/favicon-proxy/example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/favicon-proxy/not_a_host!")).StatusCode);
        Assert.DoesNotContain(app.Outbound.Blocked, u => u.Host == "example.com");
    }

    [Fact]
    public void Nothing_reached_the_real_internet()
    {
        // Every outbound request went through the fake handler: either a canned test host or refused on the spot.
        Assert.All(app.Outbound.Served, u => Assert.EndsWith(".aipulse.test", u.Host));
    }
}
