using System.Text.RegularExpressions;
using Xunit;

namespace AiPulse.Tests;

public class ThemeTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(TestData.RepoRoot, relative));

    [Theory]
    [InlineData("Components/Pages/Landing.razor")]
    [InlineData("Components/Layout/MainLayout.razor")]
    [InlineData("Components/Layout/EmptyLayout.razor")]
    public void Every_screen_a_signed_out_visitor_can_land_on_offers_the_theme_switch(string file)
    {
        Assert.Contains("<ThemeToggle", Read(file));
    }

    [Fact]
    public void Theme_script_follows_the_operating_system_and_survives_blocked_storage()
    {
        var app = Read("Components/App.razor");
        Assert.Contains("prefers-color-scheme: dark", app);
        Assert.Contains("name=\"theme-color\"", app);
        // Both storage reads and writes are wrapped; an unguarded access throws in some private modes and would break the page.
        Assert.Matches(new Regex(@"try\s*\{\s*return localStorage\.getItem"), app);
        Assert.Matches(new Regex(@"try\s*\{\s*localStorage\.setItem"), app);
    }

    [Fact]
    public void Dark_theme_redefines_every_color_token_the_light_theme_defines()
    {
        var css = Read("wwwroot/app.css");
        var light = TokensOf(css, @":root\s*\{");
        var dark = TokensOf(css, @"\[data-theme=""dark""\]\s*\{");
        // Fonts, spacing and motion are theme-independent and simply inherit; only colors and shadows must be redefined.
        var shared = new[] { "--ap-font-", "--ap-space-", "--ap-ease-", "--ap-dur-", "--ap-radius" };
        var missing = light.Except(dark).Where(t => !shared.Any(t.StartsWith)).ToList();
        Assert.True(missing.Count == 0, "Defined for light but not dark: " + string.Join(", ", missing));
    }

    private static HashSet<string> TokensOf(string css, string selectorPattern)
    {
        var start = Regex.Match(css, selectorPattern);
        Assert.True(start.Success, "selector not found: " + selectorPattern);
        var end = css.IndexOf('}', start.Index);
        var block = css[start.Index..end];
        return Regex.Matches(block, @"(--ap-[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();
    }
}
