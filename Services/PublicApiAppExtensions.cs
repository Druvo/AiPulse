using AiPulse.Services.Api;

namespace AiPulse.Services;

/// <summary>
/// One-line wiring for the public JSON API: <c>app.MapPublicApi();</c> in Program.cs, with the other endpoint mappings. Lives in
/// AiPulse.Services (already imported by Program.cs) so no extra using is needed. The API needs no service registrations of its
/// own: its parts are built here from services the app already registers (FeedHistoryService, KnowledgeBaseService,
/// FeedAggregatorService, the DbContext factory). Limits come from the optional "PublicApi" configuration section.
/// </summary>
public static class PublicApiAppExtensions
{
    /// <summary>Production wiring: resolves the app's existing services and maps the API.</summary>
    public static RouteGroupBuilder MapPublicApi(this WebApplication app)
    {
        var options = new PublicApiOptions();
        app.Configuration.GetSection("PublicApi").Bind(options);
        var runtime = new PublicApiRuntime
        {
            Data = ActivatorUtilities.CreateInstance<AppPublicApiData>(app.Services),
            Tokens = ActivatorUtilities.CreateInstance<ApiTokenService>(app.Services),
            Limiter = new ApiRateLimiter(),
            Options = options,
            Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AiPulse.PublicApi")
        };
        app.Lifetime.ApplicationStopping.Register(runtime.Limiter.Dispose);
        return app.MapPublicApi(runtime);
    }

}
