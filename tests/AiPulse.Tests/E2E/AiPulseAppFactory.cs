using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiPulse.Models;
using AiPulse.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiPulse.Tests.E2E;

/// <summary>
/// Hosts the real app (Program.cs, real middleware, real SQLite) in memory for end-to-end tests, with:
/// - a fresh temporary content root, so App_Data (database, keys, history) is created from nothing and the developer's own
///   App_Data, appsettings.Development.json and secrets are never read;
/// - every outbound HttpClient made through IHttpClientFactory answered by <see cref="FakeOutbound"/>: canned feeds for
///   *.aipulse.test hosts, an immediate failure for everything else, so no test touches the internet;
/// - the app's own background jobs (feed watcher, free-API discovery, anything added later) removed, so nothing polls behind
///   the tests' back; the ops console's event listener stays because it only subscribes;
/// - a seed of three test sources (two canned, one that always fails) and a few history items;
/// - every log entry captured, so a test can assert the server logged no errors.
/// </summary>
public sealed class AiPulseAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUser = "admin";
    public const string ReaderUser = "e2e-reader";
    public const string SeededTag = "Agents";

    public string ContentRoot { get; }
    public string AdminPassword { get; } = RandomPassword();
    public string ReaderPassword { get; } = RandomPassword();
    public LogSink Logs { get; } = new();
    public FakeOutbound Outbound { get; } = new();

    public AiPulseAppFactory()
    {
        ContentRoot = Path.Combine(Path.GetTempPath(), "aipulse-e2e-" + Guid.NewGuid().ToString("N")[..12]);
        var data = Directory.CreateDirectory(Path.Combine(ContentRoot, "Data")).FullName;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestData.RepoRoot, "Data"), "*.json"))
            File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(data, "sources.json"), FakeOutbound.SourcesJson);
    }

    private static string RandomPassword() => "e2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(ContentRoot);
        // UseSetting (not ConfigureAppConfiguration) so Program.cs sees these while it is still registering services.
        builder.UseSetting("Auth:Username", AdminUser);
        builder.UseSetting("Auth:Password", AdminPassword);
        builder.UseSetting("Auth:RequireApproval", "false");
        builder.UseSetting("Notifications:PollMinutes", "60");
        builder.UseSetting("Logging:LogLevel:Default", "Information");

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            var appAssembly = typeof(Program).Assembly;
            var background = services.Where(d => d.ServiceType == typeof(IHostedService) && IsAppJob(d, appAssembly)).ToList();
            foreach (var d in background) services.Remove(d);

            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => new FakeOutboundHandler(Outbound)));
        });
    }

    private static bool IsAppJob(ServiceDescriptor d, Assembly appAssembly)
    {
        var type = d.ImplementationType ?? d.ImplementationInstance?.GetType() ?? d.ImplementationFactory?.Method.DeclaringType;
        if (type is null || type.Assembly != appAssembly) return false;
        return !(type.FullName ?? "").Contains("PollStatusAttacher", StringComparison.Ordinal);
    }

    public async Task InitializeAsync()
    {
        _ = Server; // start the host (runs Program.cs's startup: schema, seed, bootstrap admin)
        var users = Services.GetRequiredService<UserService>();
        if (await users.GetByUsernameAsync(ReaderUser) is null)
            await users.CreateApprovedUserAsync(ReaderUser, ReaderPassword, UserRoles.User);

        Services.GetRequiredService<FeedHistoryService>().Record(FakeOutbound.HistoryItems());
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(ContentRoot, recursive: true); }
        catch (IOException) { /* a file still held open on Windows: the OS temp cleaner will get it */ }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    /// <summary>A client that keeps cookies and does not follow redirects, so tests can see where the app sends them.</summary>
    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private readonly ConcurrentDictionary<string, Task<HttpClient>> _signedIn = new();

    /// <summary>Signs in through the real /login form (antiforgery token and all) once per user and reuses that client.</summary>
    public Task<HttpClient> SignedInAs(string username) =>
        _signedIn.GetOrAdd(username, u => SignInAsync(u, u == AdminUser ? AdminPassword : ReaderPassword));

    public async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = Anonymous();
        var response = await PostLoginAsync(client, username, password);
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Sign-in as {username} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return client;
    }

    public static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string username, string password, string? token = null)
    {
        token ??= await LoginTokenAsync(client);
        var form = new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = password,
            ["Input.Remember"] = "true",
        };
        return await client.PostAsync("/login", new FormUrlEncodedContent(form));
    }

    public static async Task<string> LoginTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The login page has no antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Fails the test if the server logged anything at Error or above (an unhandled exception in a request is logged that way).</summary>
    public void AssertNoServerErrors()
    {
        var errors = Logs.Entries.Where(e => e.Level >= LogLevel.Error).ToList();
        Assert.True(errors.Count == 0, "The server logged errors:\n" + string.Join("\n---\n", errors.Select(e => e.ToString())));
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<AiPulseAppFactory>
{
    public const string Name = "AiPulse end-to-end";
}

public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception)
{
    public override string ToString() => $"[{Level}] {Category}: {Message}{(Exception is null ? "" : "\n" + Exception)}";
}

/// <summary>Collects every log entry the app writes.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) sink._entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
        }
    }
}

/// <summary>The only "internet" the app sees in these tests.</summary>
public sealed class FakeOutbound
{
    public const string WireHost = "wire.aipulse.test";
    public const string BrokenHost = "broken.aipulse.test";

    private readonly ConcurrentQueue<Uri> _blocked = new();
    private readonly ConcurrentQueue<Uri> _served = new();

    /// <summary>Requests refused because they were not for a canned test host.</summary>
    public IReadOnlyList<Uri> Blocked => _blocked.ToArray();
    public IReadOnlyList<Uri> Served => _served.ToArray();

    public static readonly string SourcesJson = JsonSerializer.Serialize(new object[]
    {
        new { Name = "E2E Wire", Url = $"https://{WireHost}/news.atom", Category = "News", ContentType = "News", Level = "Intermediate", Tags = new[] { SeededTag } },
        new { Name = "E2E Releases", Url = $"https://{WireHost}/releases.atom", Category = "Tools", ContentType = "Release", Level = "Intermediate", Tags = Array.Empty<string>() },
        new { Name = "E2E Broken", Url = $"https://{BrokenHost}/feed.xml", Category = "Community", ContentType = "Discussion", Level = "Beginner", Tags = Array.Empty<string>() },
    });

    private static string SeededTag => AiPulseAppFactory.SeededTag;

    public static IEnumerable<FeedItem> HistoryItems()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            yield return new FeedItem
            {
                Title = $"E2E headline {i}: agents ship a new framework",
                Link = $"https://{WireHost}/posts/{i}",
                Summary = "A short summary of the E2E test story about agents.",
                Published = now.AddHours(-i),
                SourceName = "E2E Wire",
                Category = "News",
                ContentType = "News",
                Tags = new[] { SeededTag },
            };
        }
    }

    internal HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (!uri.Host.Equals(WireHost, StringComparison.OrdinalIgnoreCase))
        {
            _blocked.Enqueue(uri);
            throw new HttpRequestException($"Outbound HTTP is blocked in end-to-end tests ({uri.Host}).");
        }

        _served.Enqueue(uri);
        var release = uri.AbsolutePath.Contains("releases", StringComparison.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var entries = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var id = $"https://{WireHost}/{(release ? "r" : "n")}/{i}";
            entries.Append($"<entry><title>{(release ? "E2E release v1." + i : "E2E live story " + i)}</title><link href=\"{id}\"/><id>{id}</id>" +
                           $"<updated>{now.AddMinutes(-i * 7):O}</updated><summary>Canned test content {i}.</summary></entry>");
        }
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\"><title>E2E</title><id>https://{WireHost}/</id>" +
                  $"<updated>{now:O}</updated>{entries}</feed>";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"), RequestMessage = request };
    }
}

internal sealed class FakeOutboundHandler(FakeOutbound outbound) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(outbound.Answer(request));
    }
}
