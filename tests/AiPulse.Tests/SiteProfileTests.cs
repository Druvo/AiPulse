using System.Net;
using System.Text.Json;
using AiPulse.Services;
using AiPulse.Tests.E2E;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiPulse.Tests;

public class SiteProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "site-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, "site-profile.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void A_fresh_install_is_AiPulse_about_AI()
    {
        var site = new SiteProfileService(PathIn);
        Assert.Equal("AiPulse", site.Name);
        Assert.Equal("AI", site.Topic);
        Assert.Equal("AI news", site.TopicNews);
        Assert.Equal("AiPulse - latest AI news", site.FeedTitle);
    }

    [Fact]
    public void An_admins_choice_is_applied_at_once_and_survives_a_restart()
    {
        var site = new SiteProfileService(PathIn);
        Assert.Null(site.Save("DevHub", "News for .NET developers", "Everything .NET.", ".NET"));
        Assert.Equal("DevHub - latest .NET news", site.FeedTitle);

        var after = new SiteProfileService(PathIn);
        Assert.Equal("DevHub", after.Name);
        Assert.Equal(".NET", after.Topic);
        Assert.Equal("Everything .NET.", after.Description);
    }

    [Fact]
    public void Reset_goes_back_to_the_defaults_and_removes_the_saved_file()
    {
        var site = new SiteProfileService(PathIn);
        site.Save("DevHub", "t", "d", ".NET");
        site.Reset();

        Assert.Equal("AiPulse", site.Name);
        Assert.False(File.Exists(PathIn));
        Assert.Equal("AiPulse", new SiteProfileService(PathIn).Name);
    }

    [Fact]
    public void Configuration_sets_the_identity_when_nothing_is_saved_and_a_saved_choice_wins_over_it()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:Name"] = "CloudPulse", ["Site:Topic"] = "Cloud", ["Site:Tagline"] = "Cloud news"
        }).Build();

        var site = new SiteProfileService(PathIn, config);
        Assert.Equal("CloudPulse", site.Name);
        Assert.Equal("Cloud", site.Topic);

        site.Save("Mine", "t", "d", "Rust");
        Assert.Equal("Mine", new SiteProfileService(PathIn, config).Name);
    }

    [Theory]
    [InlineData("", "t", "d", "AI", "name")]
    [InlineData("   ", "t", "d", "AI", "name")]
    [InlineData("OK", "t", "d", "", "about")]
    [InlineData("<script>", "t", "d", "AI", "plain text")]
    [InlineData("OK", "t>", "d", "AI", "plain text")]
    [InlineData("OK", "t", "d<b>", "AI", "plain text")]
    public void Unacceptable_values_are_refused_with_a_reason_and_nothing_is_saved(string name, string tagline, string description, string topic, string mentions)
    {
        var site = new SiteProfileService(PathIn);
        var error = site.Save(name, tagline, description, topic);

        Assert.NotNull(error);
        Assert.Contains(mentions, error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("AiPulse", site.Name);
        Assert.False(File.Exists(PathIn));
    }

    [Fact]
    public void Lengths_are_bounded()
    {
        Assert.NotNull(SiteSettings.Validate(new string('a', SiteSettings.MaxName + 1), "t", "d", "AI").Error);
        Assert.NotNull(SiteSettings.Validate("n", new string('a', SiteSettings.MaxTagline + 1), "d", "AI").Error);
        Assert.NotNull(SiteSettings.Validate("n", "t", new string('a', SiteSettings.MaxDescription + 1), "AI").Error);
        Assert.NotNull(SiteSettings.Validate("n", "t", "d", new string('a', SiteSettings.MaxTopic + 1)).Error);
        Assert.Null(SiteSettings.Validate(new string('a', SiteSettings.MaxName), "t", "d", "AI").Error);
    }

    [Fact]
    public void Newlines_are_flattened_and_blank_optional_fields_fall_back_to_the_defaults()
    {
        var (settings, error) = SiteSettings.Validate("Dev\nHub", "", "", "Web");
        Assert.Null(error);
        Assert.Equal("Dev Hub", settings!.Name);
        Assert.Equal(SiteSettings.Default.Tagline, settings.Tagline);
        Assert.Equal(SiteSettings.Default.Description, settings.Description);
    }

    [Fact]
    public void A_corrupt_saved_file_is_ignored_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ nope");
        Assert.Equal("AiPulse", new SiteProfileService(PathIn).Name);
    }
}

[Collection(E2ECollection.Name)]
public class SiteProfileEndpointTests(AiPulseAppFactory app)
{
    [Fact]
    public async Task The_whole_site_uses_the_chosen_name_and_topic_then_goes_back()
    {
        var site = app.Services.GetRequiredService<SiteProfileService>();
        site.Save("DevHub", "News for developers", "A hub for developers.", "Web");
        try
        {
            var client = app.Anonymous();

            var landing = await client.GetStringAsync("/");
            Assert.Contains("DevHub", landing);
            Assert.Contains("News for developers", landing);
            // What may still say AiPulse: the stylesheet bundle name, the project's repository link its copyright line and the clone-and-run sample.
            Assert.DoesNotContain("AiPulse", landing.Replace("AiPulse.styles.css", "").Replace("github.com/Druvo/AiPulse", "").Replace("AiPulse contributors", "").Replace("cd AiPulse", ""));

            Assert.Contains("<title>DevHub · Help</title>", await client.GetStringAsync("/help"));

            using var manifest = JsonDocument.Parse(await client.GetStringAsync("/manifest.webmanifest"));
            Assert.Equal("DevHub", manifest.RootElement.GetProperty("name").GetString());
            Assert.Equal("News for developers", manifest.RootElement.GetProperty("description").GetString());
            Assert.True(manifest.RootElement.TryGetProperty("icons", out _));   // the rest of the template is intact

            Assert.Contains("DevHub - latest Web news", await client.GetStringAsync("/feed.xml"));
        }
        finally { site.Reset(); }

        Assert.Equal("AiPulse", JsonDocument.Parse(await app.Anonymous().GetStringAsync("/manifest.webmanifest")).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_manifest_is_served_with_the_manifest_content_type()
    {
        var response = await app.Anonymous().GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
    }
}
