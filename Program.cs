using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AiPulse.Components;
using AiPulse.Models;
using AiPulse.Services;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// HttpClient used for feed fetching. A real User-Agent stops some hosts (Reddit etc.) from blocking us.
builder.Services.AddHttpClient("feeds", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AiPulse/1.0 (+https://localhost; personal AI news dashboard)");
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
});

// HttpClient for the Explore page's live lookups (Hugging Face trending, GitHub search/API, and the
// scraped github.com/trending pages - hence both JSON and HTML in Accept). GitHub's API requires a
// User-Agent on every request or it responds 403.
builder.Services.AddHttpClient("explore", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AiPulse/1.0 (+https://localhost; personal AI news dashboard)");
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0.9));
});

// HttpClient for talking to a locally-running Ollama instance (see OllamaService). No fixed short
// timeout here - pulling a model can legitimately take many minutes; individual calls set their own.
builder.Services.AddHttpClient("ollama", c =>
{
    c.Timeout = TimeSpan.FromMinutes(30);
});

// Sources are dynamically managed (add/edit/remove from the Sources page) and persisted in SQLite.
// A factory (not a plain DbContext) because KnowledgeBaseService is a singleton, and DbContext instances
// must be short-lived/scoped-per-operation - see KnowledgeBaseService for how it's used.
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "aipulse.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContextFactory<AiPulseDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

// Core app services.
builder.Services.AddSingleton<KnowledgeBaseService>();
builder.Services.AddSingleton<FeedAggregatorService>();
// Scoped (one instance per signed-in circuit) - these resolve the current user via AuthenticationStateProvider.
builder.Services.AddScoped<ReadingStateService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton<FeedHistoryService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddScoped<ObsidianExportService>();
builder.Services.AddSingleton<HuggingFaceService>();
builder.Services.AddSingleton<TrendingRepoHistoryService>();
builder.Services.AddSingleton<GitHubTrendingService>();
builder.Services.AddSingleton<OllamaService>();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<ModelUsageService>();
builder.Services.AddScoped<ChatHistoryService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<OpmlService>();
builder.Services.AddSingleton<SourceHealthService>();
builder.Services.AddSingleton<ContentExtractorService>();
builder.Services.AddSingleton<FeedDiscoveryService>();
builder.Services.AddSingleton<FaviconService>();
builder.Services.AddHttpContextAccessor();

// WebSub (PubSubHubbub): subscribe to feeds that push updates instead of polling. Inert unless
// WebSub:PublicBaseUrl is configured - a hub can't call back to "localhost".
builder.Services.Configure<WebSubOptions>(builder.Configuration.GetSection("WebSub"));
builder.Services.AddSingleton<WebSubService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<FeverApiService>();
builder.Services.AddSingleton<FreeApiDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FreeApiDiscoveryService>());

// Background watcher that raises desktop notifications for big releases & watchlist hits.
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddHostedService<FeedWatcherService>();

// AI hook: standalone by default. Swap NullSummarizer for a real implementation to enable AI.
builder.Services.AddSingleton<ISummarizer, NullSummarizer>();

// Email hook: standalone by default. Password-reset links always work (shown in the server log, or
// via Users.razor's admin "Generate reset link" button) - only real delivery needs Smtp:Host configured.
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
if (string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
    builder.Services.AddSingleton<IEmailSender, NullEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// --- Authentication (cookie based, single configured user) ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// Google/GitHub sign-in: credentials are admin-configurable at runtime (Settings.razor), stored in the
// DB rather than appsettings.json - see OAuthSettingsService for why the handlers below are always
// registered even when neither provider is configured yet.
builder.Services.AddSingleton<OAuthSettingsService>();
builder.Services.AddSingleton<ExternalAuthService>();
// Registered under IConfigureOptions<T> (the base interface), not IConfigureNamedOptions<T> directly -
// OptionsFactory<T> only resolves IEnumerable<IConfigureOptions<T>> from DI and checks "is
// IConfigureNamedOptions<T>" on each entry itself; registering under the derived interface means DI never
// surfaces it there and the configurator silently never runs.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<GoogleOptions>, GoogleOptionsConfigurator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<GitHubAuthenticationOptions>, GitHubOptionsConfigurator>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "AiPulse.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Session-invalidation check: an admin's "Force logout," a password reset, or a Disable action all
        // bump AppUser.SecurityStamp. This compares that against the stamp baked into the session's own
        // claim on each request, so those actions actually take effect instead of just blocking future
        // logins. Throttled to once/minute per request (not exactly per-request) since it's a DB read -
        // costs nothing on Blazor Server's DB-free static asset/SignalR traffic, negligible on real navigation.
        options.Events.OnValidatePrincipal = async context =>
        {
            var lastCheckedRaw = context.Properties.Items.TryGetValue("stamp_checked", out var raw) ? raw : null;
            if (lastCheckedRaw is not null && DateTimeOffset.TryParse(lastCheckedRaw, out var lastChecked)
                && DateTimeOffset.UtcNow - lastChecked < TimeSpan.FromMinutes(1))
                return;

            var userIdClaim = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var stampClaim = context.Principal?.FindFirst("security_stamp")?.Value;
            if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
            var user = await users.GetByIdAsync(userId);
            if (user is null || user.Status != AiPulse.Models.UserStatuses.Approved || user.SecurityStamp != stampClaim)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            context.Properties.Items["stamp_checked"] = DateTimeOffset.UtcNow.ToString("o");
            context.ShouldRenew = true;
        };
    })
    .AddGoogle(options =>
    {
        // ClientId/ClientSecret come from GoogleOptionsConfigurator (DB-backed), not set here.
        options.CallbackPath = "/signin-google";
        options.Events.OnTicketReceived = ctx => HandleExternalTicketReceivedAsync(ctx, OAuthProviders.Google);
    })
    .AddGitHub(options =>
    {
        // ClientId/ClientSecret come from GitHubOptionsConfigurator (DB-backed), not set here.
        options.CallbackPath = "/signin-github";
        options.Scope.Add("user:email"); // GitHub omits email from the base profile unless requested
        options.Events.OnTicketReceived = ctx => HandleExternalTicketReceivedAsync(ctx, OAuthProviders.GitHub);
    });
builder.Services.AddAuthorization();

// Shared by both providers' OnTicketReceived above: resolve/create the local AppUser (ExternalAuthService),
// then replace the provider's own principal with AiPulse's own 4-claim shape (same one Login.razor's
// SignIn builds) before letting the framework's default post-TicketReceived flow sign in under the
// cookie scheme and redirect to whatever RedirectUri the /auth/external/{provider} endpoint set below.
async Task HandleExternalTicketReceivedAsync(TicketReceivedContext ctx, string provider)
{
    var externalAuth = ctx.HttpContext.RequestServices.GetRequiredService<ExternalAuthService>();
    var (result, user) = await externalAuth.CompleteAsync(provider, ctx.Principal!);

    if (result != ExternalLoginResult.Success || user is null)
    {
        ctx.HandleResponse();
        var reason = result == ExternalLoginResult.PendingApproval ? "pending" : "disabled";
        ctx.Response.Redirect($"/login?error={reason}");
        return;
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim("security_stamp", user.SecurityStamp)
    };
    ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
}
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Force revalidation on every request for our own static assets (app.css, notify.js, icons) instead of
// letting the browser cache them heuristically - without this, editing app.css/notify.js during
// development (or after a self-hoster pulls an update) can silently keep serving stale JS/CSS from the
// browser's disk cache indefinitely, since ASP.NET Core's static file middleware sends no Cache-Control
// header by default. Revalidation is cheap (a 304 when unchanged) and guarantees correctness.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Sign out and return to the login page.
app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Kicks off a Google/GitHub sign-in. A minimal-API endpoint rather than something Login.razor calls
// directly - Blazor Interactive Server components run over SignalR and can't issue a Challenge result themselves.
app.MapGet("/auth/external/{provider}", async (string provider, OAuthSettingsService oauthSettings, string? returnUrl) =>
{
    var scheme = provider.ToLowerInvariant() switch
    {
        "google" => OAuthProviders.Google,
        "github" => OAuthProviders.GitHub,
        _ => null
    };
    if (scheme is null) return Results.NotFound();
    if (!await oauthSettings.IsUsableAsync(scheme)) return Results.Redirect("/login");

    var safeReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/dashboard";
    return Results.Challenge(new AuthenticationProperties { RedirectUri = safeReturnUrl }, [scheme]);
});

// Generated at request time from the request's own Host header, not a static file - AiPulse is
// self-hosted with no fixed domain, so a committed sitemap.xml would bake one deployment's URL (e.g.
// the maintainer's own demo instance) into every other self-hoster's copy of the repo.
app.MapGet("/sitemap.xml", (HttpRequest request) =>
{
    var origin = $"{request.Scheme}://{request.Host}";
    var xml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>{origin}/</loc><changefreq>weekly</changefreq><priority>1.0</priority></url>
          <url><loc>{origin}/register</loc><changefreq>monthly</changefreq><priority>0.5</priority></url>
          <url><loc>{origin}/login</loc><changefreq>monthly</changefreq><priority>0.3</priority></url>
        </urlset>
        """;
    return Results.Text(xml, "application/xml");
});

// Dev-only helper: generate a PBKDF2 hash to paste into appsettings ("Auth:PasswordHash").
if (app.Environment.IsDevelopment())
{
    app.MapGet("/auth/hash", (string password) => Results.Text(PasswordHasher.Hash(password)));

}

// Backup/restore App_Data (sources, bookmarks, chat history, feed history) as a single zip.
app.MapGet("/backup/download", (BackupService backup) =>
{
    var bytes = backup.CreateBackupZip();
    return Results.File(bytes, "application/zip", $"aipulse-backup-{DateTime.Now:yyyy-MM-dd}.zip");
}).RequireAuthorization();

app.MapPost("/backup/restore", async (HttpRequest request, BackupService backup) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("No file uploaded.");

    await using var stream = file.OpenReadStream();
    await backup.RestoreFromZipAsync(stream);
    return Results.Redirect("/settings?restored=true");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// Per-source favicon, fetched and cached by AiPulse itself (never a third-party favicon CDN - see
// FaviconService for why). {host} is validated as a bare hostname before any fetch happens.
app.MapGet("/favicon-proxy/{host}", async (string host, FaviconService favicons) =>
{
    if (Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.Basic)
        return Results.BadRequest();

    var favicon = await favicons.GetFaviconAsync(host);
    return favicon is null ? Results.NotFound() : Results.File(favicon.Value.Bytes, favicon.Value.ContentType);
}).RequireAuthorization();

// Bulk import/export of sources via OPML - the standard RSS-reader interchange format.
app.MapGet("/opml/export", (OpmlService opml) =>
    Results.Text(opml.ExportOpml(), "text/x-opml", Encoding.UTF8))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/opml/import", async (HttpRequest request, OpmlService opml) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("No file uploaded.");

    await using var stream = file.OpenReadStream();
    var (added, skipped, unreachable) = await opml.ImportOpmlAsync(stream);
    return Results.Redirect($"/sources?opmlAdded={added}&opmlSkipped={skipped}&opmlUnreachable={unreachable}");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// WebSub callback: hubs are anonymous external servers, so these two endpoints are deliberately
// unauthenticated. Correctness/security instead comes from (a) matching hub.topic against what we
// actually subscribed to on the GET verification handshake, and (b) requiring a valid HMAC signature
// (computed from a per-subscription secret only we and the hub know) on every POST content push.
app.MapGet("/websub/callback/{sourceId:int}", async (int sourceId, HttpRequest request, WebSubService webSub) =>
{
    var mode = request.Query["hub.mode"].ToString();
    var topic = request.Query["hub.topic"].ToString();
    var challenge = request.Query["hub.challenge"].ToString();
    int? lease = int.TryParse(request.Query["hub.lease_seconds"], out var l) ? l : null;

    var echoed = await webSub.HandleVerificationAsync(sourceId, mode, topic, challenge, lease);
    return echoed is null ? Results.NotFound() : Results.Text(echoed, "text/plain");
});

app.MapPost("/websub/callback/{sourceId:int}", async (int sourceId, HttpRequest request, WebSubService webSub, KnowledgeBaseService kb, FeedAggregatorService feeds, FeedHistoryService history) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var bodyBytes = ms.ToArray();

    var signature = request.Headers["X-Hub-Signature"].FirstOrDefault() ?? request.Headers["X-Hub-Signature-256"].FirstOrDefault();
    if (!await webSub.VerifyPushSignatureAsync(sourceId, bodyBytes, signature))
        return Results.Unauthorized();

    var source = kb.SourceRecords.FirstOrDefault(s => s.Id == sourceId)?.ToFeedSource();
    if (source is null) return Results.NotFound();

    var xml = Encoding.UTF8.GetString(bodyBytes);
    var pushedItems = await feeds.MergePushedContentAsync(source, xml);
    history.Record(pushedItems);
    return Results.Ok();
});

// Fever API - lets mobile RSS clients (Reeder, ReadKit, Fiery Feeds, etc.) read AiPulse's feed. Unauthenticated
// at the ASP.NET Core level like the WebSub callback (mobile apps don't send our login cookie); auth is the
// protocol's own api_key, checked per-user inside FeverApiService. Force feed refresh isn't triggered here -
// it reads whatever FeedHistoryService already has, which the background watcher keeps current.
app.MapMethods("/fever/", new[] { "GET", "POST" }, async (HttpRequest request, FeverApiService fever) =>
{
    IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync() : null;
    var apiKey = form?["api_key"].FirstOrDefault() ?? request.Query["api_key"].FirstOrDefault();
    var userKey = fever.Authenticate(apiKey);

    var response = await fever.BuildResponseAsync(userKey, request.Query, form);
    return Results.Json(response);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Ensure the sources DB exists and is seeded from Data/sources.json on first run.
using (var scope = app.Services.CreateScope())
{
    var kb = scope.ServiceProvider.GetRequiredService<KnowledgeBaseService>();
    await kb.InitializeSourcesAsync();
    await kb.InitializeFreeApisAsync();

    var authOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
    await scope.ServiceProvider.GetRequiredService<UserService>().EnsureSchemaAndBootstrapAsync();

    // Upgrade path: move pre-multi-user reading-state.json into the bootstrapped Admin's per-user folder.
    ReadingStateService.MigrateLegacyStateFile(app.Environment, authOptions.Username);

    // One-time repair for items/bookmarks recorded before the "(untitled)" fix - no-op on subsequent runs.
    var history = scope.ServiceProvider.GetRequiredService<FeedHistoryService>();
    var repairedTitles = history.RepairUntitledTitles();
    var repairedBookmarks = ReadingStateService.RepairUntitledBookmarksForAllUsers(app.Environment, history);
    if (repairedTitles > 0 || repairedBookmarks > 0)
        app.Logger.LogInformation("Repaired {Titles} historical item title(s) and {Bookmarks} bookmark title(s) that were \"(untitled)\"", repairedTitles, repairedBookmarks);
}

app.Run();
