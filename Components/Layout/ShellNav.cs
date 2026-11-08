namespace AiPulse.Components.Layout;

/// <summary>One link in the signed-in app shell.</summary>
/// <param name="Path">Relative route ("news"), no leading slash, so it resolves against &lt;base href="/"&gt; like the rest of the app.</param>
/// <param name="Label">Shown in the sidebar and drawer; also the tooltip and accessible name when the sidebar is collapsed to its icon rail.</param>
/// <param name="Icon">An <c>Icon</c> component name.</param>
/// <param name="TabLabel">Short label for the phone bottom tab bar; null keeps the item out of the tab bar.</param>
/// <param name="ExactMatch">Highlight only on this exact route (the Dashboard), not on every route that starts with it.</param>
public sealed record ShellNavItem(string Path, string Label, string Icon, string? TabLabel = null, bool ExactMatch = false);

/// <summary>A titled group of links in the sidebar ("Overview", "Learning", "Admin").</summary>
public sealed record ShellNavSection(string Title, bool AdminOnly, IReadOnlyList<ShellNavItem> Items);

/// <summary>
/// The single list behind the signed-in navigation: the desktop sidebar, its collapsed icon rail, the phone slide-over menu and the phone
/// bottom tab bar are all rendered from it, so they can never disagree about what a section is called, which icon it has or where it goes.
/// </summary>
public static class ShellNav
{
    public static readonly IReadOnlyList<ShellNavSection> Sections = new[]
    {
        new ShellNavSection("Overview", AdminOnly: false, new[]
        {
            new ShellNavItem("dashboard", "Dashboard", "home", TabLabel: "Home", ExactMatch: true),
            new ShellNavItem("news", "News Feed", "rss", TabLabel: "News"),
            new ShellNavItem("ask", "Ask AiPulse", "sparkle", TabLabel: "Ask"),
            new ShellNavItem("explore", "Explore", "trending-up"),
            new ShellNavItem("free-apis", "Free AI APIs", "webhook"),
        }),
        new ShellNavSection("Learning", AdminOnly: false, new[]
        {
            new ShellNavItem("learn", "Learning Hub", "graduation-cap", TabLabel: "Learn"),
            new ShellNavItem("reading-stats", "Reading Stats", "activity"),
            new ShellNavItem("glossary", "Glossary", "book"),
            new ShellNavItem("tools", "Tools & Tips", "tool"),
        }),
        new ShellNavSection("Admin", AdminOnly: true, new[]
        {
            new ShellNavItem("playground", "Playground", "message"),
            new ShellNavItem("sources", "Sources", "database"),
            new ShellNavItem("ai-settings", "AI Settings", "zap"),
            new ShellNavItem("source-health", "Source Health", "activity"),
            new ShellNavItem("users", "Users", "users"),
        }),
    };

    /// <summary>Sections a person may see: admin-only groups are dropped for everyone else.</summary>
    public static IEnumerable<ShellNavSection> VisibleTo(bool isAdmin) => Sections.Where(s => isAdmin || !s.AdminOnly);

    /// <summary>
    /// The phone bottom tab bar: the main signed-in destinations, in sidebar order. A fifth "Menu" button (rendered by the layout, not
    /// listed here) opens the full slide-over menu for everything else. Kept to four links so each tab stays a comfortable 44px+ target at 320px.
    /// </summary>
    public static IReadOnlyList<ShellNavItem> Tabs { get; } =
        Sections.Where(s => !s.AdminOnly).SelectMany(s => s.Items).Where(i => i.TabLabel is not null).ToList();

    /// <summary>
    /// One or two letters for the avatar circle in the user menu: the first letters of the first two words ("Ada Lovelace" -> "AL"),
    /// or of a single word's first two letter/digit characters ("druvo" -> "DR", "j.smith" -> "JS"). Never empty.
    /// </summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var words = name.Split(new[] { ' ', '.', '_', '-', '@' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1)
            return words[0].Length == 1 ? words[0].ToUpperInvariant() : words[0][..2].ToUpperInvariant();
        return string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
    }
}
