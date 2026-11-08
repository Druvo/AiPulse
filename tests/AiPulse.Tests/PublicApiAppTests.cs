using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiPulse.Tests;

/// <summary>
/// The whole app (Program.cs) in memory, to prove the one-line wiring works next to the cookie auth, antiforgery and Razor
/// endpoints. Runs from a temp content root (a copy of Data/, a fresh App_Data) with background pollers removed, so it never
/// touches the network or a real database.
/// </summary>
public sealed class PublicApiAppFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "aipulse-app-tests", Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<ApiTokenService> Factory { get; private set; } = default!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Data"));
        foreach (var file in Directory.GetFiles(Path.Combine(TestData.RepoRoot, "Data"), "*.json"))
            File.Copy(file, Path.Combine(Root, "Data", Path.GetFileName(file)));

        Factory = new WebApplicationFactory<ApiTokenService>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseContentRoot(Root);
            b.UseSetting("Auth:Username", "admin");
            b.UseSetting("Auth:Password", "test-only-" + Guid.NewGuid().ToString("N"));
            b.UseSetting("PublicApi:AnonymousPermitLimit", "1000");
            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

public class PublicApiAppTests : IClassFixture<PublicApiAppFixture>
{
    private readonly PublicApiAppFixture _app;
    public PublicApiAppTests(PublicApiAppFixture app) => _app = app;

    private HttpClient Client() => _app.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Program_maps_the_api_with_live_data()
    {
        using var client = Client();
        var status = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("database").GetString());

        var seeded = JsonSerializer.Deserialize<List<FeedSource>>(File.ReadAllText(Path.Combine(_app.Root, "Data", "sources.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var sources = JsonDocument.Parse(await client.GetStringAsync("/api/v1/sources")).RootElement;
        Assert.Equal(seeded.Count(s => s.Enabled), sources.GetProperty("meta").GetProperty("total").GetInt32());

        var glossary = JsonDocument.Parse(await client.GetStringAsync("/api/v1/glossary")).RootElement;
        Assert.Equal(TestData.Load<GlossaryTerm>("glossary.json").Count, glossary.GetProperty("meta").GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/news")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task A_token_created_through_the_apps_own_database_authenticates_me()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);

        var dbFactory = _app.Factory.Services.GetRequiredService<IDbContextFactory<AiPulseDbContext>>();
        var admin = await _app.Factory.Services.GetRequiredService<UserService>().GetByUsernameAsync("admin");
        var (created, _) = await new ApiTokenService(dbFactory).CreateAsync(admin!.Id, "app test", TimeSpan.FromDays(1));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created!.Token);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_developers_page_is_public_and_documents_every_endpoint()
    {
        using var client = Client();
        var res = await client.GetAsync("/developers");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("<h1", html);
        Assert.Contains("Developers", html);
        foreach (var e in ApiCatalog.Endpoints)
            Assert.Contains($"id=\"op-{e.OperationId}\"", html);
        foreach (var code in new[] { "invalid-parameter", "unauthorized", "invalid-token", "not-found", "rate-limited", "server-error", "method-not-allowed", "insufficient-scope" })
            Assert.Contains($"id=\"error-{code}\"", html);
        Assert.Contains("feed.xml", html);
        Assert.Contains("/api/v1/openapi.json", html);
    }

    [Fact]
    public async Task Robots_and_sitemap_include_the_developers_page()
    {
        using var client = Client();
        Assert.Contains("Allow: /developers", await client.GetStringAsync("/robots.txt"));
        Assert.Contains("/developers</loc>", await client.GetStringAsync("/sitemap.xml"));
    }
}
