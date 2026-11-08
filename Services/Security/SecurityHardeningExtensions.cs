using AiPulse.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// In AiPulse.Services (not .Security) so Program.cs needs no extra using for its single AddSecurityHardening line.
namespace AiPulse.Services;

public static class SecurityHardeningExtensions
{
    /// <summary>
    /// Everything in the "account security" layer, from one call in Program.cs: TOTP two-factor sign-in (services plus the
    /// sign-in guard), security headers with a report-only CSP and its /csp-report sink, and rate limits on the sign-in and
    /// recovery endpoints. The middleware is attached through an IStartupFilter so it wraps the whole pipeline.
    /// </summary>
    public static IServiceCollection AddSecurityHardening(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<SecurityHardeningOptions>();
        if (configuration is not null) options.Bind(configuration.GetSection("Security"));

        services.AddDataProtection(); // idempotent; Program.cs sets the "AiPulse" application name and persists the key ring
        services.TryAddSingleton<TwoFactorService>();
        services.TryAddSingleton<TwoFactorLoginFlow>();
        services.TryAddSingleton<CspViolationLog>();
        services.TryAddSingleton<ClerkCspHostSource>();

        // Wrap the framework's authentication service. Registered so it wins whether AddAuthentication runs before or after:
        // existing registrations are removed, and AddAuthentication only TryAdds its own.
        services.RemoveAll<IAuthenticationService>();
        services.AddScoped<IAuthenticationService>(sp => new TwoFactorSignInGuard(
            ActivatorUtilities.CreateInstance<AuthenticationService>(sp),
            sp.GetRequiredService<TwoFactorService>(),
            sp.GetRequiredService<TwoFactorLoginFlow>(),
            sp.GetRequiredService<IOptions<SecurityHardeningOptions>>()));

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<SecurityHardeningOptions>>((limiter, security) =>
        {
            if (!security.Value.RateLimitingEnabled) return;
            limiter.GlobalLimiter = SecurityRateLimits.CreateLimiter();
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, token) =>
            {
                var http = context.HttpContext;
                var rule = SecurityRateLimits.Match(http.Request.Method, http.Request.Path);
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("AiPulse.Security.RateLimit")
                    .LogWarning("Rate limit {Rule} hit by {Client} on {Path}", rule?.Name, SecurityRateLimits.ClientKey(http), http.Request.Path.Value);
                http.Response.Headers.RetryAfter = ((int)(rule?.Window.TotalSeconds ?? 60)).ToString();
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync("Too many attempts from your network. Please wait a minute and try again.", token);
            };
        });

        services.AddTransient<IStartupFilter, SecurityStartupFilter>();
        return services;
    }

    private sealed class SecurityStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseRateLimiter();
            app.UseMiddleware<CspReportMiddleware>();
            next(app);
        };
    }
}
