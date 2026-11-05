using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiPulse.Services.Productivity;

/// <summary>
/// Wiring for the productivity features (command palette, saved searches + alerts, bookmark collections, reading mode, the weekly
/// digest page/feed/email and the onboarding checklist). Program.cs calls exactly these two methods.
/// </summary>
public static class ProductivityExtensions
{
    public static WebApplicationBuilder AddProductivityFeatures(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "aipulse.db");
        services.AddDbContextFactory<ProductivityDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<ProductivityDb>();
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<SavedSearchAlertOptions>(builder.Configuration.GetSection("SavedSearchAlerts"));
        services.Configure<DigestOptions>(builder.Configuration.GetSection("Digest"));

        services.AddSingleton<SavedSearchService>();
        services.AddSingleton<SavedSearchAlertService>();
        services.AddHostedService(sp => sp.GetRequiredService<SavedSearchAlertService>());

        services.AddSingleton<BookmarkCollectionService>();
        services.AddSingleton<ReaderService>();

        services.AddSingleton<ProductivitySettingsService>();
        services.AddSingleton<DigestUnsubscribeTokens>();
        services.AddSingleton<WeeklyDigestSender>();
        services.AddHostedService<WeeklyDigestBackgroundService>();
        return builder;
    }

    public static WebApplication MapProductivityEndpoints(this WebApplication app)
    {
        // Public Atom feed of the weekly digest: one entry per ISO week (the current week updates in place).
        app.MapGet("/this-week.xml", (HttpRequest request, FeedHistoryService history, TimeProvider time) =>
            Results.Text(WeeklyDigest.Atom(history.Items, $"{request.Scheme}://{request.Host}", time.GetUtcNow()), "application/atom+xml; charset=utf-8"));
        return app;
    }
}
