using System.Net.Http.Headers;
using AiPulse.Components;
using AiPulse.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

// HttpClient for the Explore page's live lookups (Hugging Face trending, GitHub search).
// GitHub's API requires a User-Agent on every request or it responds 403.
builder.Services.AddHttpClient("explore", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AiPulse/1.0 (+https://localhost; personal AI news dashboard)");
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
builder.Services.AddSingleton<ReadingStateService>();
builder.Services.AddSingleton<FeedHistoryService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<ObsidianExportService>();
builder.Services.AddSingleton<HuggingFaceService>();
builder.Services.AddSingleton<GitHubTrendingService>();
builder.Services.AddSingleton<OllamaService>();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<ModelUsageService>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHttpContextAccessor();

// Background watcher that raises desktop notifications for big releases & watchlist hits.
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddHostedService<FeedWatcherService>();

// AI hook: standalone by default. Swap NullSummarizer for a real implementation to enable AI.
builder.Services.AddSingleton<ISummarizer, NullSummarizer>();

// --- Authentication (cookie based, single configured user) ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "AiPulse.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
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

// Dev-only helper: generate a PBKDF2 hash to paste into appsettings ("Auth:PasswordHash").
if (app.Environment.IsDevelopment())
{
    app.MapGet("/auth/hash", (string password) => Results.Text(AuthService.CreateHash(password)));
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
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Ensure the sources DB exists and is seeded from Data/sources.json on first run.
using (var scope = app.Services.CreateScope())
{
    var kb = scope.ServiceProvider.GetRequiredService<KnowledgeBaseService>();
    await kb.InitializeSourcesAsync();
}

app.Run();
