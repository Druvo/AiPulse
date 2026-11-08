namespace AiPulse.Services.Productivity;

public enum PaletteKind { Page, Action }

/// <summary>
/// One entry in the command palette. <see cref="Href"/> for pages; <see cref="Id"/> names the action otherwise. <see cref="GoKey"/> is
/// the second key of a "g then key" shortcut (e.g. 'n' for g n → News), when the entry has one.
/// </summary>
public sealed record PaletteCommand(
    string Id,
    string Title,
    PaletteKind Kind,
    string? Href = null,
    string Keywords = "",
    string Icon = "zap",
    char? GoKey = null,
    bool SignedInOnly = false,
    bool SignedOutOnly = false,
    string[]? Roles = null);

/// <summary>Everything the palette and the global shortcuts can reach, filtered by who is asking. Pure, so access rules are testable.</summary>
public static class CommandCatalog
{
    public const string ToggleTheme = "toggle-theme";
    public const string MarkAllRead = "mark-all-read";
    public const string RefreshFeed = "refresh-feed";
    public const string SignOut = "sign-out";
    public const string ShowShortcuts = "show-shortcuts";

    private static readonly string[] Admin = { "Admin" };

    public static readonly IReadOnlyList<PaletteCommand> All = new[]
    {
        new PaletteCommand("page-dashboard", "Dashboard", PaletteKind.Page, "/dashboard", "home overview start", "home", 'h', SignedInOnly: true),
        new PaletteCommand("page-home", "Home", PaletteKind.Page, "/", "landing start", "home", 'h', SignedOutOnly: true),
        new PaletteCommand("page-news", "News Feed", PaletteKind.Page, "/news", "headlines articles feed unread", "rss", 'n'),
        new PaletteCommand("page-this-week", "This week in AI", PaletteKind.Page, "/this-week", "weekly digest summary roundup", "calendar", 'w'),
        new PaletteCommand("page-topics", "Topics", PaletteKind.Page, "/topics", "tags subjects", "trending-up", 't'),
        new PaletteCommand("page-explore", "Explore", PaletteKind.Page, "/explore", "trending models repos github hugging face", "trending-up", 'e'),
        new PaletteCommand("page-releases", "Releases", PaletteKind.Page, "/releases", "versions changelog", "download", 'r'),
        new PaletteCommand("page-glossary", "Glossary", PaletteKind.Page, "/glossary", "terms jargon definitions", "book", 'g'),
        new PaletteCommand("page-tools", "Tools & Tips", PaletteKind.Page, "/tools", "tools matrix tips", "tool", 'o'),
        new PaletteCommand("page-free-apis", "Free AI APIs", PaletteKind.Page, "/free-apis", "api keys providers", "webhook", SignedInOnly: true),
        new PaletteCommand("page-ask", "Ask AiPulse", PaletteKind.Page, "/ask", "chat question assistant", "sparkle", 'a', SignedInOnly: true),
        new PaletteCommand("page-learn", "Learning Hub", PaletteKind.Page, "/learn", "courses modules roadmap", "graduation-cap", 'l', SignedInOnly: true),
        new PaletteCommand("page-reading-stats", "Reading Stats", PaletteKind.Page, "/reading-stats", "history streak bookmarks reading list", "activity", SignedInOnly: true),
        new PaletteCommand("page-saved", "Saved searches", PaletteKind.Page, "/saved", "alerts rules queries notifications", "search", 's', SignedInOnly: true),
        new PaletteCommand("page-collections", "Bookmark collections", PaletteKind.Page, "/collections", "bookmarks saved folders notes export", "bookmark", 'b', SignedInOnly: true),
        new PaletteCommand("page-settings", "Settings", PaletteKind.Page, "/settings", "preferences account profile webhook digest", "settings", 'p', SignedInOnly: true),
        new PaletteCommand("page-help", "Help", PaletteKind.Page, "/help", "guide docs faq", "help-circle"),
        new PaletteCommand("page-coverage", "Sources we follow", PaletteKind.Page, "/coverage", "coverage sources list", "database"),
        new PaletteCommand("page-login", "Sign in", PaletteKind.Page, "/login", "login account", "user", SignedOutOnly: true),
        new PaletteCommand("page-register", "Get started (create account)", PaletteKind.Page, "/register", "register sign up", "user", SignedOutOnly: true),
        new PaletteCommand("page-playground", "Playground", PaletteKind.Page, "/playground", "admin prompt test", "message", Roles: Admin),
        new PaletteCommand("page-sources", "Sources", PaletteKind.Page, "/sources", "admin feeds opml", "database", Roles: Admin),
        new PaletteCommand("page-ai-settings", "AI Settings", PaletteKind.Page, "/ai-settings", "admin providers models keys", "zap", Roles: Admin),
        new PaletteCommand("page-source-health", "Source Health", PaletteKind.Page, "/source-health", "admin errors failing feeds", "activity", Roles: Admin),
        new PaletteCommand("page-users", "Users", PaletteKind.Page, "/users", "admin accounts approve roles", "user", Roles: Admin),

        new PaletteCommand(ToggleTheme, "Toggle light / dark theme", PaletteKind.Action, Keywords: "dark mode light mode appearance", Icon: "moon"),
        new PaletteCommand(ShowShortcuts, "Show keyboard shortcuts", PaletteKind.Action, Keywords: "keys help hotkeys", Icon: "help-circle"),
        new PaletteCommand(MarkAllRead, "Mark all news as read", PaletteKind.Action, Keywords: "read clear unread inbox zero", Icon: "check", SignedInOnly: true),
        new PaletteCommand(RefreshFeed, "Refresh the news feed", PaletteKind.Action, Keywords: "reload poll fetch update", Icon: "refresh", SignedInOnly: true),
        new PaletteCommand(SignOut, "Sign out", PaletteKind.Action, Keywords: "log out logout exit", Icon: "logout", SignedInOnly: true),
    };

    /// <summary>The entries this visitor may use: signed-out visitors get public pages; role-gated pages need one of their roles.</summary>
    public static IReadOnlyList<PaletteCommand> For(bool signedIn, IEnumerable<string>? roles = null)
    {
        var roleSet = new HashSet<string>(roles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return All.Where(c =>
        {
            if (c.SignedOutOnly && signedIn) return false;
            if ((c.SignedInOnly || c.Roles is not null) && !signedIn) return false;
            if (c.Roles is not null && !c.Roles.Any(roleSet.Contains)) return false;
            return true;
        }).ToList();
    }

    /// <summary>"g then key" navigation shortcuts for this visitor: key → path. One path per key.</summary>
    public static IReadOnlyDictionary<char, string> GoShortcuts(bool signedIn, IEnumerable<string>? roles = null) =>
        For(signedIn, roles).Where(c => c.Kind == PaletteKind.Page && c.GoKey is not null && c.Href is not null)
            .GroupBy(c => c.GoKey!.Value).ToDictionary(g => g.Key, g => g.First().Href!);

    /// <summary>Ranks commands for a query (fuzzy over title and keywords). An empty query returns them in catalog order.</summary>
    public static IReadOnlyList<PaletteCommand> Search(IReadOnlyList<PaletteCommand> commands, string? query, int take = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return commands.Take(take).ToList();
        return commands
            .Select(c => (c, Score: Best(FuzzyMatcher.Score(query, c.Title), FuzzyMatcher.Score(query, c.Keywords) - 20)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score).ThenBy(x => x.c.Title, StringComparer.OrdinalIgnoreCase)
            .Take(take).Select(x => x.c).ToList();
    }

    private static int? Best(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

/// <summary>
/// Small fuzzy matcher for the palette: the query's characters must appear in order (case-insensitive). Scores reward a contiguous
/// substring, a match at the start, matches at word starts and runs of consecutive characters, and penalise gaps. Null = no match.
/// </summary>
public static class FuzzyMatcher
{
    public static int? Score(string? query, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var q = (query ?? "").Trim();
        if (q.Length == 0) return 0;
        if (q.Length > text.Length) return null;

        // A contiguous match is best at the very start, then at a word start; mid-word it ranks below an acronym match
        // ("rs" finds "Reading Stats" before "Courses").
        var substring = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (substring >= 0)
        {
            var bonus = substring == 0 ? 1000 : IsWordStart(text, substring) ? 800 : 300;
            return bonus - substring - (text.Length - q.Length) / 2;
        }

        var total = 0;
        var ti = 0;
        var lastMatch = -2;
        var allWordStarts = true;
        foreach (var qc in q)
        {
            if (char.IsWhiteSpace(qc)) continue;
            var found = -1;
            // Prefer the next occurrence that starts a word, so "rs" lines up with Reading Stats' initials.
            for (var k = ti; k < text.Length; k++)
                if (char.ToLowerInvariant(text[k]) == char.ToLowerInvariant(qc) && IsWordStart(text, k)) { found = k; break; }
            if (found < 0)
                for (var k = ti; k < text.Length; k++)
                    if (char.ToLowerInvariant(text[k]) == char.ToLowerInvariant(qc)) { found = k; break; }
            if (found < 0) return null;
            ti = found + 1;
            total += 10;
            if (found == lastMatch + 1) total += 15;
            if (IsWordStart(text, found)) total += 25; else allWordStarts = false;
            if (found == 0) total += 20;
            if (lastMatch >= 0) total -= Math.Min(10, found - lastMatch - 1);
            lastMatch = found;
        }
        if (allWordStarts) total += 400;
        return total - text.Length / 4;
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
