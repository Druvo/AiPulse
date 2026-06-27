using System.Net.Http.Headers;
using AiPulse.Components;
using AiPulse.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

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

// Core app services.
builder.Services.AddSingleton<KnowledgeBaseService>();
builder.Services.AddSingleton<FeedAggregatorService>();
builder.Services.AddSingleton<ReadingStateService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<ObsidianExportService>();
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

app.UseStaticFiles();
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
