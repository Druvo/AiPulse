using System.Net;
using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiPulse.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;
    private long _ticks;
    public ManualTime(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) { _now += by; _ticks += by.Ticks; }
}

internal sealed class SimpleDbFactory : IDbContextFactory<AiPulseDbContext>
{
    private readonly DbContextOptions<AiPulseDbContext> _options;
    public SimpleDbFactory(string connectionString) =>
        _options = new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite(connectionString).Options;
    public AiPulseDbContext CreateDbContext() => new(_options);
}

/// <summary>A throwaway SQLite database with the Users table bootstrapped (an Admin "admin") and a token service over it.</summary>
internal sealed class TempDb : IAsyncDisposable
{
    public string Path { get; }
    public string ConnectionString { get; }
    public SimpleDbFactory Factory { get; }
    public UserService Users { get; }

    private TempDb(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Factory = new SimpleDbFactory(ConnectionString);
        Users = new UserService(Factory, Options.Create(new AuthOptions { Username = "admin", Password = "test-only-" + Guid.NewGuid().ToString("N") }),
            new NullEmailSender(NullLogger<NullEmailSender>.Instance), NullLogger<UserService>.Instance);
    }

    public static async Task<TempDb> CreateAsync(bool bootstrapUsers = true)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aipulse-api-tests");
        Directory.CreateDirectory(dir);
        var db = new TempDb(System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db"));
        if (bootstrapUsers) await db.Users.EnsureSchemaAndBootstrapAsync();
        return db;
    }

    public ApiTokenService Tokens(TimeProvider? time = null) => new(Factory, time);

    public async Task<AppUser> UserAsync(string username, string role = UserRoles.User) =>
        await Users.CreateApprovedUserAsync(username, "pw-" + Guid.NewGuid().ToString("N"), role);

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(Path); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeApiData : IPublicApiData
{
    public List<FeedItem> ItemList { get; set; } = new();
    public IReadOnlyList<FeedItem> Items => ItemList.OrderByDescending(i => i.Published).ToList();
    public List<FeedSource> SourceList { get; set; } = new();
    public IReadOnlyList<FeedSource> Sources => SourceList;
    public List<GlossaryTerm> GlossaryList { get; set; } = new();
    public IReadOnlyList<GlossaryTerm> Glossary => GlossaryList;
    public List<ToolEntry> ToolList { get; set; } = new();
    public IReadOnlyList<ToolEntry> Tools => ToolList;
    public int LearningModules { get; set; } = 3;
    public DateTimeOffset? LastPolled { get; set; }
    public int PollMinutes { get; set; } = 15;
    public Dictionary<string, ReadingState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DbHealthy { get; set; } = true;

    public ReadingState? ReadingStateFor(string username) => States.TryGetValue(username, out var s) ? s : null;
    public Task<bool> DatabaseHealthyAsync(CancellationToken ct) => Task.FromResult(DbHealthy);

    public static FeedItem Item(string title, DateTimeOffset published, string source = "Example Blog", string type = "News",
        string[]? tags = null, string category = "News", string level = "Intermediate", string? summary = null) => new()
    {
        Title = title,
        Link = "https://example.com/" + Uri.EscapeDataString(title.ToLowerInvariant().Replace(' ', '-')),
        Summary = summary ?? $"<p>About {title}.</p>",
        Published = published,
        SourceName = source,
        Category = category,
        ContentType = type,
        Level = level,
        Tags = tags ?? Array.Empty<string>()
    };

    /// <summary>A small, varied data set anchored on <paramref name="now"/>.</summary>
    public static FakeApiData Sample(DateTimeOffset now)
    {
        var data = new FakeApiData { LastPolled = now.AddMinutes(-5) };
        data.ItemList.AddRange(new[]
        {
            Item("Agents get memory", now.AddHours(-1), tags: new[] { "agents", "LLM" }),
            Item("RAG pipelines in practice", now.AddHours(-3), source: "Research Weekly", category: "Research", type: "Paper", tags: new[] { "RAG" }, level: "Advanced"),
            Item("New agent framework", now.AddHours(-30), source: "Dev Digest", tags: new[] { "Agents", "tools" }, type: "Tutorial", level: "Beginner"),
            Item("Mixture of experts explained", now.AddDays(-3), tags: new[] { "MoE", "agents" }, summary: "Sparse mixture of experts routing."),
            Item("Old news about agents", now.AddDays(-40), tags: new[] { "agents" }),
            Item("transformers v5.0.0", now.AddHours(-2), source: "GitHub Releases", type: "Release", category: "Tools", tags: new[] { "release" }),
            Item("llama.cpp b4000", now.AddDays(-2), source: "GitHub Releases", type: "Release", category: "Tools"),
            Item("Future release", now.AddDays(2), source: "GitHub Releases", type: "Release", category: "Tools"),
        });
        data.SourceList.AddRange(new[]
        {
            new FeedSource { Name = "Example Blog", Url = "https://example.com/feed.xml", Category = "News", Tags = new[] { "ai" } },
            new FeedSource { Name = "Research Weekly", Url = "https://research.example.org/rss", Category = "Research", ContentType = "Paper" },
            new FeedSource { Name = "Local scrape", Url = "not a url", Category = "Community" },
        });
        data.GlossaryList.AddRange(new[]
        {
            new GlossaryTerm { Term = "Retrieval-Augmented Generation", Aliases = new[] { "RAG" }, Category = "Technique", ShortDef = "Ground answers in retrieved documents." },
            new GlossaryTerm { Term = "Mixture of Experts", Aliases = new[] { "MoE" }, Category = "Architecture", ShortDef = "Route tokens to a few expert sub-networks." },
            new GlossaryTerm { Term = "Agent", Category = "Concept", ShortDef = "A model that plans and calls tools in a loop." },
        });
        data.ToolList.AddRange(new[]
        {
            new ToolEntry { Name = "Claude Code", Category = "Coding Agent", OneLiner = "Agentic coding in the terminal.", BestFor = new[] { "multi-file changes" }, Url = "https://claude.com" },
            new ToolEntry { Name = "Ollama", Category = "Local Runtime", OneLiner = "Run models locally." },
        });
        return data;
    }
}

/// <summary>A minimal in-memory web host running only the API, over fixed data and a temp database.</summary>
internal sealed class ApiHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required PublicApiRuntime Runtime { get; init; }
    public required FakeApiData Data { get; init; }
    public required TempDb Db { get; init; }
    public required ManualTime Time { get; init; }

    public static async Task<ApiHost> StartAsync(PublicApiOptions? options = null, FakeApiData? data = null, ManualTime? time = null)
    {
        time ??= new ManualTime();
        data ??= FakeApiData.Sample(time.GetUtcNow());
        var db = await TempDb.CreateAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();

        // Tests choose the caller's address with a header, since TestServer has none.
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-IP", out var ip) && IPAddress.TryParse(ip, out var parsed))
                ctx.Connection.RemoteIpAddress = parsed;
            return next(ctx);
        });

        var runtime = new PublicApiRuntime
        {
            Data = data,
            Tokens = db.Tokens(time),
            Limiter = new ApiRateLimiter(time),
            Options = options ?? new PublicApiOptions { AnonymousPermitLimit = 1000, TokenPermitLimit = 1000 },
            Time = time
        };
        app.MapPublicApi(runtime);
        await app.StartAsync();
        return new ApiHost { App = app, Client = app.GetTestClient(), Runtime = runtime, Data = data, Db = db, Time = time };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Runtime.Limiter.Dispose();
        await Db.DisposeAsync();
    }
}
