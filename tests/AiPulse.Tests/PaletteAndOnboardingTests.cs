using AiPulse.Services.Productivity;
using Xunit;

namespace AiPulse.Tests;

public class CommandCatalogTests
{
    [Fact]
    public void Signed_out_visitors_get_public_pages_and_safe_actions_only()
    {
        var cmds = CommandCatalog.For(signedIn: false);
        Assert.Contains(cmds, c => c.Href == "/news");
        Assert.Contains(cmds, c => c.Href == "/login");
        Assert.Contains(cmds, c => c.Id == CommandCatalog.ToggleTheme);
        Assert.DoesNotContain(cmds, c => c.Href == "/dashboard");
        Assert.DoesNotContain(cmds, c => c.Href == "/users");
        Assert.DoesNotContain(cmds, c => c.Id is CommandCatalog.SignOut or CommandCatalog.MarkAllRead or CommandCatalog.RefreshFeed);
    }

    [Fact]
    public void Signed_in_users_get_their_pages_but_not_admin_ones()
    {
        var cmds = CommandCatalog.For(signedIn: true, new[] { "User" });
        Assert.Contains(cmds, c => c.Href == "/dashboard");
        Assert.Contains(cmds, c => c.Href == "/saved");
        Assert.Contains(cmds, c => c.Id == CommandCatalog.SignOut);
        Assert.DoesNotContain(cmds, c => c.Href is "/users" or "/sources" or "/playground" or "/ai-settings" or "/source-health");
        Assert.DoesNotContain(cmds, c => c.Href == "/login");
    }

    [Fact]
    public void Admins_get_admin_pages_regardless_of_role_case()
    {
        var cmds = CommandCatalog.For(signedIn: true, new[] { "admin" });
        Assert.Contains(cmds, c => c.Href == "/users");
        Assert.Contains(cmds, c => c.Href == "/sources");
    }

    [Fact]
    public void Every_page_command_points_at_a_real_route_with_matching_sign_in_rules()
    {
        var routes = typeof(AiPulse.Services.PublicSite).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false).Cast<Microsoft.AspNetCore.Components.RouteAttribute>()
                .Select(r => (r.Template, Gated: t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Any())))
            .ToDictionary(r => r.Template, r => r.Gated, StringComparer.OrdinalIgnoreCase);
        foreach (var c in CommandCatalog.All.Where(c => c.Kind == PaletteKind.Page))
        {
            Assert.True(routes.ContainsKey(c.Href!), $"{c.Href} has no page");
            if (routes[c.Href!]) Assert.True(c.SignedInOnly || c.Roles is not null, $"{c.Href} needs sign-in but the palette offers it to visitors");
        }
    }

    [Fact]
    public void Go_shortcuts_are_role_aware_and_unique()
    {
        var anon = CommandCatalog.GoShortcuts(false);
        Assert.Equal("/news", anon['n']);
        Assert.Equal("/", anon['h']);
        Assert.False(anon.ContainsKey('s'));
        var user = CommandCatalog.GoShortcuts(true);
        Assert.Equal("/dashboard", user['h']);
        Assert.Equal("/saved", user['s']);
        Assert.Equal("/collections", user['b']);
    }

    [Fact]
    public void Search_ranks_title_prefix_matches_first_and_uses_keywords()
    {
        var cmds = CommandCatalog.For(true, new[] { "Admin" });
        Assert.Equal("News Feed", CommandCatalog.Search(cmds, "news")[0].Title);
        Assert.Equal(CommandCatalog.ToggleTheme, CommandCatalog.Search(cmds, "dark mode")[0].Id);
        Assert.Equal("/glossary", CommandCatalog.Search(cmds, "jargon")[0].Href);
        Assert.Empty(CommandCatalog.Search(cmds, "zzzzqqq"));
        Assert.Equal(5, CommandCatalog.Search(cmds, "", take: 5).Count);
    }
}

public class FuzzyMatcherTests
{
    [Fact]
    public void Characters_must_appear_in_order()
    {
        Assert.NotNull(FuzzyMatcher.Score("rdst", "Reading Stats"));
        Assert.Null(FuzzyMatcher.Score("tsdr", "Reading Stats"));
        Assert.Null(FuzzyMatcher.Score("x", ""));
        Assert.Equal(0, FuzzyMatcher.Score("", "anything"));
    }

    [Fact]
    public void Substrings_beat_scattered_matches_and_prefixes_beat_inner_substrings()
    {
        Assert.True(FuzzyMatcher.Score("set", "Settings") > FuzzyMatcher.Score("set", "AI Settings"));
        Assert.True(FuzzyMatcher.Score("set", "AI Settings") > FuzzyMatcher.Score("set", "Source Health Tests"));
    }

    [Fact]
    public void Word_start_matches_beat_mid_word_matches()
    {
        Assert.True(FuzzyMatcher.Score("rs", "Reading Stats") > FuzzyMatcher.Score("rs", "Courses"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(FuzzyMatcher.Score("NEWS", "News Feed"), FuzzyMatcher.Score("news", "News Feed"));
    }
}

public class OnboardingTests
{
    private static readonly HashSet<string> None = new();

    [Fact]
    public void Steps_are_detected_from_signals()
    {
        var steps = OnboardingSteps.Steps(new OnboardingSignals(2, 0, 1, false), None);
        Assert.True(steps.Single(s => s.Key == OnboardingSteps.Topics).Done);
        Assert.False(steps.Single(s => s.Key == OnboardingSteps.Sources).Done);
        Assert.True(steps.Single(s => s.Key == OnboardingSteps.SavedSearch).Done);
        Assert.Equal((2, 5), OnboardingSteps.Progress(steps));
    }

    [Fact]
    public void Install_and_two_factor_can_be_ticked_by_hand_but_detected_steps_cannot()
    {
        var manual = OnboardingSteps.ParseManual("install,2fa,topics,bogus");
        Assert.Equal(new[] { "2fa", "install" }, manual.OrderBy(x => x));
        var steps = OnboardingSteps.Steps(new OnboardingSignals(0, 0, 0, false), manual);
        Assert.True(steps.Single(s => s.Key == OnboardingSteps.Install).Done);
        Assert.True(steps.Single(s => s.Key == OnboardingSteps.TwoFactor).Done);
        Assert.False(steps.Single(s => s.Key == OnboardingSteps.Topics).Done);
    }

    [Fact]
    public void Installed_app_is_detected_without_a_manual_tick()
    {
        var steps = OnboardingSteps.Steps(new OnboardingSignals(0, 0, 0, true), None);
        Assert.True(steps.Single(s => s.Key == OnboardingSteps.Install).Done);
    }

    [Fact]
    public void Toggle_adds_and_removes_and_ignores_unknown_keys()
    {
        var s = OnboardingSteps.Toggle("", "install", true);
        Assert.Equal("install", s);
        s = OnboardingSteps.Toggle(s, "2fa", true);
        Assert.Equal("2fa,install", s);
        s = OnboardingSteps.Toggle(s, "install", false);
        Assert.Equal("2fa", s);
        Assert.Equal("2fa", OnboardingSteps.Toggle(s, "topics", true));
    }

    [Fact]
    public async Task Dismissal_and_manual_steps_persist_per_user()
    {
        using var db = new TestProductivityDb();
        var svc = new ProductivitySettingsService(db.Db);
        Assert.False((await svc.GetAsync(1)).OnboardingDismissed);
        await svc.UpdateAsync(1, s => { s.OnboardingDismissed = true; s.OnboardingManualSteps = OnboardingSteps.Toggle(s.OnboardingManualSteps, "install", true); });
        var again = new ProductivitySettingsService(db.Db);
        Assert.True((await again.GetAsync(1)).OnboardingDismissed);
        Assert.Equal("install", (await again.GetAsync(1)).OnboardingManualSteps);
        Assert.False((await again.GetAsync(2)).OnboardingDismissed);
    }
}
