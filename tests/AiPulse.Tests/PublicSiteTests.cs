using System.Reflection;
using System.Xml.Linq;
using AiPulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace AiPulse.Tests;

public class PublicSiteTests
{
    private sealed record Route(string Template, Type Page, bool NeedsSignIn);

    /// <summary>Every routable Razor page in the app with whether it demands a signed-in user.</summary>
    private static readonly List<Route> Routes = typeof(PublicSite).Assembly.GetTypes()
        .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => new Route(r.Template, t, t.GetCustomAttributes<AuthorizeAttribute>().Any())))
        .ToList();

    private static Route? Find(string path) => Routes.FirstOrDefault(r => string.Equals(r.Template, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Every_menu_and_entry_page_exists_and_opens_without_signing_in()
    {
        foreach (var page in PublicSite.Menu.Concat(PublicSite.Entry).Append(new PublicPage("/", "Home", "weekly", 1)))
        {
            var route = Find(page.Path);
            Assert.True(route is not null, $"{page.Path} is in the public list but no page has that route.");
            Assert.False(route!.NeedsSignIn, $"{page.Path} is in the public menu/sitemap but {route.Page.Name} requires sign-in.");
        }
    }

    [Fact]
    public void Every_page_open_to_anyone_is_listed_so_the_menu_sitemap_and_robots_cannot_forget_it()
    {
        var known = new HashSet<string>(
            PublicSite.Menu.Select(p => p.Path).Concat(PublicSite.Entry.Select(p => p.Path)).Concat(PublicSite.UnlistedPublicRoutes).Append("/"),
            StringComparer.OrdinalIgnoreCase);

        var unlisted = Routes.Where(r => !r.NeedsSignIn && !known.Contains(r.Template)).Select(r => $"{r.Template} ({r.Page.Name})").ToList();

        Assert.True(unlisted.Count == 0,
            "These pages need no sign-in but are not in PublicSite (add to Menu/Entry, or to UnlistedPublicRoutes if they should stay out of the menu and sitemap): " + string.Join(", ", unlisted));
    }

    [Fact]
    public void Pages_that_require_sign_in_are_never_in_the_public_menu_or_sitemap()
    {
        var gated = Routes.Where(r => r.NeedsSignIn).Select(r => r.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var leaked = PublicSite.Menu.Select(p => p.Path).Concat(PublicSite.Entry.Select(p => p.Path)).Concat(PublicSite.CrawlablePaths).Where(gated.Contains).Distinct().ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public void The_unlisted_routes_all_exist_so_the_list_cannot_go_stale()
    {
        foreach (var template in PublicSite.UnlistedPublicRoutes.Where(t => t != "/Error"))
            Assert.True(Find(template) is not null, $"{template} is listed as an unlisted public route but no page has it.");
    }

    [Fact]
    public void Menu_labels_are_unique_and_paths_are_absolute()
    {
        Assert.Equal(PublicSite.Menu.Count, PublicSite.Menu.Select(p => p.Label).Distinct().Count());
        Assert.All(PublicSite.Menu.Concat(PublicSite.Entry), p => Assert.StartsWith("/", p.Path));
    }

    [Fact]
    public void The_sitemap_lists_the_landing_page_the_whole_menu_and_the_entry_pages_for_the_hosts_own_origin()
    {
        var urls = XDocument.Parse(PublicSite.Sitemap("https://example.test"))
            .Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value).ToList();

        Assert.Equal("https://example.test/", urls[0]);
        foreach (var page in PublicSite.Menu.Concat(PublicSite.Entry))
            Assert.Contains($"https://example.test{page.Path}", urls);
        Assert.Equal(1 + PublicSite.Menu.Count + PublicSite.Entry.Count, urls.Count);
        Assert.Equal(urls.Count, urls.Distinct().Count());
    }

    [Fact]
    public void Robots_allows_exactly_the_public_pages_blocks_the_rest_and_points_at_the_sitemap()
    {
        var robots = PublicSite.Robots("https://example.test");
        var lines = robots.Split('\n').Select(l => l.Trim()).ToList();

        Assert.Contains("Allow: /$", lines);
        foreach (var page in PublicSite.Menu)
            Assert.Contains($"Allow: {page.Path}", lines);
        Assert.Contains("Allow: /login", lines);
        Assert.Contains("Disallow: /", lines);
        Assert.Contains("Sitemap: https://example.test/sitemap.xml", lines);

        var allowed = lines.Where(l => l.StartsWith("Allow: ")).Select(l => l["Allow: ".Length..]).Where(p => p != "/$");
        var gated = Routes.Where(r => r.NeedsSignIn).Select(r => r.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(allowed, gated.Contains);
    }

    [Fact]
    public void Disallow_comes_after_the_allows_so_specific_rules_win_for_crawlers_that_read_in_order()
    {
        var lines = PublicSite.Robots("https://example.test").Split('\n').Select(l => l.Trim()).ToList();
        Assert.True(lines.IndexOf("Disallow: /") > lines.FindLastIndex(l => l.StartsWith("Allow: ")));
    }
}
