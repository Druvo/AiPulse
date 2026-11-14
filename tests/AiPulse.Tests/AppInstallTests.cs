using AiPulse.Services;
using Xunit;

namespace AiPulse.Tests;

public class AppInstallTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1", DeviceKind.Ios)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148", DeviceKind.Ios)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36", DeviceKind.Android)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", DeviceKind.Desktop)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15", DeviceKind.Desktop)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Gecko/20100101 Firefox/120.0", DeviceKind.Desktop)]
    [InlineData("curl/8.4.0", DeviceKind.Other)]
    [InlineData("", DeviceKind.Desktop)]
    [InlineData(null, DeviceKind.Desktop)]
    public void Devices_are_told_apart_from_the_user_agent(string? ua, DeviceKind expected) => Assert.Equal(expected, AppInstall.Classify(ua));

    [Fact]
    public void Android_wins_over_the_Linux_token_every_Android_agent_contains()
    {
        Assert.Equal(DeviceKind.Android, AppInstall.Classify("Mozilla/5.0 (Linux; Android 13; SM-S918B) Chrome/119 Mobile"));
    }

    [Theory]
    [InlineData("https://aipulse.example/", "https://aipulse.example/app")]
    [InlineData("https://aipulse.example", "https://aipulse.example/app")]
    [InlineData("http://192.168.0.50:5257/", "http://192.168.0.50:5257/app")]
    public void The_shared_link_is_the_sites_own_app_page(string baseUri, string expected) => Assert.Equal(expected, AppInstall.PageUrl(baseUri));

    [Theory]
    [InlineData("https://aipulse.example/", true)]
    [InlineData("http://localhost:5257/", true)]
    [InlineData("http://127.0.0.1:5257/", true)]
    [InlineData("http://app.localhost:5000/", true)]
    [InlineData("http://192.168.0.50:5257/", false)]
    [InlineData("http://aipulse.example/", false)]
    [InlineData("not a uri", false)]
    public void Install_is_only_promised_where_browsers_allow_it(string baseUri, bool expected) => Assert.Equal(expected, AppInstall.CanInstallFrom(baseUri));

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { TestData.RepoRoot }.Concat(parts).ToArray()));

    [Fact]
    public void The_app_page_is_public_listed_and_reachable_from_the_footer_menu_and_landing_page()
    {
        Assert.Contains(PublicSite.Entry, p => p.Path == "/app");
        Assert.Contains("href=\"app\"", Read("Components", "Layout", "MainLayout.razor"));
        Assert.Contains("href=\"/app\"", Read("Components", "Pages", "Landing.razor"));
    }

    [Fact]
    public void The_install_script_is_loaded_everywhere_so_the_one_shot_install_prompt_is_never_missed()
    {
        var app = Read("Components", "App.razor");
        Assert.Contains("js/install-app.js", app);
        var js = Read("wwwroot", "js", "install-app.js");
        Assert.Contains("beforeinstallprompt", js);
        Assert.Contains("appinstalled", js);
        Assert.Contains("display-mode: standalone", js);
    }

    [Fact]
    public void The_manifest_meets_the_installability_requirements()
    {
        var m = System.Text.Json.JsonDocument.Parse(Read("wwwroot", "manifest.webmanifest")).RootElement;
        Assert.Equal("standalone", m.GetProperty("display").GetString());
        Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("start_url").GetString()));
        var sizes = m.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("sizes").GetString()).ToList();
        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
        Assert.Contains(m.GetProperty("icons").EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var shot in m.GetProperty("screenshots").EnumerateArray())
            Assert.True(File.Exists(Path.Combine(TestData.RepoRoot, "wwwroot", shot.GetProperty("src").GetString()!)), "missing screenshot " + shot);
    }
}
