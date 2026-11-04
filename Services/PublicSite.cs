using System.Text;

namespace AiPulse.Services;

public sealed record PublicPage(string Path, string Label, string ChangeFrequency, double Priority);

/// <summary>
/// The one list of what a signed-out visitor can open. The public menus (landing page and in-app top bar), sitemap.xml and
/// robots.txt are all built from it, so the menu, the sitemap and what crawlers are told cannot disagree - they once did:
/// the menu offered five public pages while robots.txt told search engines everything except the landing page was off limits.
/// </summary>
public static class PublicSite
{
    /// <summary>Pages in the public menu, in menu order. Labels match the signed-in sidebar and each page's own heading.</summary>
    public static readonly IReadOnlyList<PublicPage> Menu = new[]
    {
        new PublicPage("/news", "News Feed", "daily", 0.9),
        new PublicPage("/topics", "Topics", "daily", 0.8),
        new PublicPage("/explore", "Explore", "daily", 0.8),
        new PublicPage("/releases", "Releases", "daily", 0.7),
        new PublicPage("/glossary", "Glossary", "weekly", 0.7),
        new PublicPage("/tools", "Tools & Tips", "weekly", 0.7),
        new PublicPage("/help", "Help", "monthly", 0.5),
    };

    public const string LandingPath = "/";

    /// <summary>Entry points that are public but are not menu items.</summary>
    public static readonly IReadOnlyList<PublicPage> Entry = new[]
    {
        new PublicPage("/coverage", "Sources we follow", "weekly", 0.5),
        new PublicPage("/register", "Get started", "monthly", 0.5),
        new PublicPage("/login", "Sign in", "monthly", 0.3),
    };

    /// <summary>
    /// Routable pages that need no sign-in but are deliberately kept out of the menu and sitemap: account recovery and error
    /// screens, and member profiles (which belong to individuals, not to the site's public face).
    /// </summary>
    public static readonly IReadOnlyList<string> UnlistedPublicRoutes = new[]
    {
        "/forgot-password", "/reset-password", "/login/clerk", "/access-denied", "/u/{Username}", "/Error",
        // Reached from /topics; the sitemap lists the busiest ones individually (see Sitemap's extraPaths).
        "/topics/{Tag}"
    };

    /// <summary>Machine-readable endpoints crawlers and feed readers may fetch (not pages, so not in the menu).</summary>
    public static readonly IReadOnlyList<string> Feeds = new[] { "/feed.xml" };

    /// <summary>Every path a crawler may fetch.</summary>
    public static IEnumerable<string> CrawlablePaths =>
        new[] { LandingPath }.Concat(Menu.Select(p => p.Path)).Concat(Entry.Select(p => p.Path)).Concat(Feeds).Append("/forgot-password");

    /// <param name="extraPaths">Dynamic public pages worth indexing, e.g. the busiest /topics/{tag} pages.</param>
    public static string Sitemap(string origin, IEnumerable<string>? extraPaths = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

        void Add(string path, string frequency, double priority) =>
            sb.Append($"  <url><loc>{origin}{path}</loc><changefreq>{frequency}</changefreq><priority>{priority:0.0}</priority></url>\n");

        Add(LandingPath, "weekly", 1.0);
        foreach (var page in Menu) Add(page.Path, page.ChangeFrequency, page.Priority);
        foreach (var page in Entry) Add(page.Path, page.ChangeFrequency, page.Priority);

        foreach (var path in extraPaths ?? Enumerable.Empty<string>()) Add(path, "daily", 0.5);

        sb.Append("</urlset>\n");
        return sb.ToString();
    }

    public static string Robots(string origin)
    {
        var sb = new StringBuilder();
        sb.Append("# Lumen is mostly a signed-in dashboard, but the pages in its public menu are open to everyone and worth indexing.\n");
        sb.Append("# Everything not listed here (the dashboard, member pages, admin screens, profiles) stays off limits.\n");
        sb.Append("# Allow: /$ matches the exact root only; the rest of the site still falls under Disallow: /, because Google and Bing\n");
        sb.Append("# resolve overlapping rules by specificity.\n");
        sb.Append("User-agent: *\n");
        sb.Append("Allow: /$\n");
        foreach (var path in CrawlablePaths.Where(p => p != LandingPath))
            sb.Append($"Allow: {path}\n");
        sb.Append("Disallow: /\n\n");
        sb.Append($"Sitemap: {origin}/sitemap.xml\n");
        return sb.ToString();
    }
}
