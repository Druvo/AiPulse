using System.Net;
using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests.E2E;

/// <summary>Every page, as an anonymous visitor, a signed-in reader and an admin would get it from the real app.</summary>
[Collection(E2ECollection.Name)]
public class RouteTests(AiPulseAppFactory app)
{
    /// <summary>
    /// Public pages known to lack an h1 (FocusOnNavigate in Routes.razor focuses the h1, and screen-reader users navigate by it).
    /// /access-denied uses an h3 - reported for the owner of the auth pages to fix; remove it from here once it has an h1.
    /// </summary>
    private static readonly HashSet<string> KnownWithoutH1 = new(StringComparer.OrdinalIgnoreCase) { "/access-denied", "/Error" };

    [Fact]
    public void The_route_scan_found_the_pages()
    {
        Assert.Contains(AppRoutes.All, r => r.Template == "/news" && !r.NeedsSignIn);
        Assert.Contains(AppRoutes.All, r => r.Template == "/dashboard" && r.NeedsSignIn && !r.AdminOnly);
        Assert.Contains(AppRoutes.All, r => r.Template == "/ops" && r.AdminOnly);
    }

    [Theory]
    [MemberData(nameof(AppRoutes.PublicRoutes), MemberType = typeof(AppRoutes))]
    public async Task Public_pages_open_anonymously_with_a_title_and_heading(string template)
    {
        var client = app.Anonymous();
        var url = AppRoutes.Url(template)!;
        var response = await client.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode} {response.Headers.Location}");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        var title = AppRoutes.Title(html);
        Assert.False(string.IsNullOrWhiteSpace(title), $"{url} has no <title>");
        if (template != "/Error") Assert.Contains("AiPulse", title);
        if (!KnownWithoutH1.Contains(template)) Assert.False(string.IsNullOrWhiteSpace(AppRoutes.H1(html)), $"{url} has no <h1>");
        app.AssertNoServerErrors();
    }

    [Fact]
    public async Task Public_menu_pages_are_titled_with_their_menu_label()
    {
        var client = app.Anonymous();
        foreach (var page in PublicSite.Menu)
        {
            var html = await client.GetStringAsync(page.Path);
            var title = AppRoutes.Title(html);
            var h1 = AppRoutes.H1(html);
            Assert.True(title.Contains(page.Label, StringComparison.OrdinalIgnoreCase) || h1.Contains(page.Label, StringComparison.OrdinalIgnoreCase),
                $"{page.Path}: neither the title \"{title}\" nor the h1 \"{h1}\" mentions its menu label \"{page.Label}\"");
        }
    }

    [Fact]
    public async Task Landing_page_is_the_public_front_door()
    {
        var html = await app.Anonymous().GetStringAsync("/");
        Assert.Contains("AiPulse", AppRoutes.Title(html));
        Assert.Contains("href=\"manifest.webmanifest\"", html);
        Assert.Contains("_framework/blazor.web.js", html);
    }

    [Theory]
    [MemberData(nameof(AppRoutes.SignedInOnly), MemberType = typeof(AppRoutes))]
    [MemberData(nameof(AppRoutes.AdminOnlyRoutes), MemberType = typeof(AppRoutes))]
    public async Task Signed_in_pages_send_anonymous_visitors_to_login(string template)
    {
        var url = AppRoutes.Url(template)!;
        var response = await app.Anonymous().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/login", location);
        Assert.Contains("ReturnUrl=" + Uri.EscapeDataString(url), location);
    }

    [Theory]
    [MemberData(nameof(AppRoutes.SignedInOnly), MemberType = typeof(AppRoutes))]
    public async Task Signed_in_pages_render_for_a_reader(string template)
    {
        var client = await app.SignedInAs(AiPulseAppFactory.ReaderUser);
        var url = AppRoutes.Url(template)!;
        var response = await client.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            // A few old routes forward to their replacement; that is fine as long as it is not a bounce to sign-in or access denied.
            var location = response.Headers.Location!.ToString();
            Assert.DoesNotContain("/login", location);
            Assert.DoesNotContain("/access-denied", location);
        }
        else
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("AiPulse", AppRoutes.Title(html));
            Assert.DoesNotContain("You don't have permission", html);
        }
        app.AssertNoServerErrors();
    }

    [Theory]
    [MemberData(nameof(AppRoutes.AdminOnlyRoutes), MemberType = typeof(AppRoutes))]
    public async Task Admin_pages_are_denied_to_a_reader(string template)
    {
        var client = await app.SignedInAs(AiPulseAppFactory.ReaderUser);
        var response = await client.GetAsync(AppRoutes.Url(template)!);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/access-denied", response.Headers.Location!.ToString());
    }

    [Theory]
    [MemberData(nameof(AppRoutes.AdminOnlyRoutes), MemberType = typeof(AppRoutes))]
    public async Task Admin_pages_render_for_an_admin(string template)
    {
        var client = await app.SignedInAs(AiPulseAppFactory.AdminUser);
        var url = AppRoutes.Url(template)!;
        var response = await client.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode} {response.Headers.Location}");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("AiPulse", AppRoutes.Title(html));
        Assert.False(string.IsNullOrWhiteSpace(AppRoutes.H1(html)), $"{url} has no <h1>");
        app.AssertNoServerErrors();
    }

    [Fact]
    public async Task An_unknown_url_is_a_404_not_an_error()
    {
        var response = await app.Anonymous().GetAsync("/no-such-page-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        app.AssertNoServerErrors();
    }
}
