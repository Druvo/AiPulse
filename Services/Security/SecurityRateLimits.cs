using System.Threading.RateLimiting;

namespace AiPulse.Services.Security;

public sealed record RateLimitRule(string Name, int PermitLimit, TimeSpan Window);

/// <summary>
/// Per-client-IP limits for the anonymous endpoints people (or bots) can hammer: sign-in, the 2FA step, Clerk's session
/// exchange, external sign-in kick-off, account recovery/registration pages, and the CSP report sink. Account-level lockouts
/// (5 wrong passwords or 5 wrong codes = 15 minutes) still apply on top; these limits stop one client cycling through many
/// accounts. The register/forgot/reset forms submit over the Blazor circuit, not HTTP, so they are also throttled inside
/// UserService - the limit here caps how fast new circuits for those pages can be opened.
/// </summary>
public static class SecurityRateLimits
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    public static readonly RateLimitRule Login = new("login", 10, Minute);
    public static readonly RateLimitRule TwoFactor = new("two-factor", 10, Minute);
    public static readonly RateLimitRule ClerkSession = new("clerk-session", 10, Minute);
    public static readonly RateLimitRule ExternalSignIn = new("external-signin", 20, Minute);
    public static readonly RateLimitRule AccountPages = new("account-pages", 20, Minute);
    public static readonly RateLimitRule SignOutEverywhere = new("sign-out-everywhere", 10, Minute);
    public static readonly RateLimitRule CspReport = new("csp-report", 60, Minute);
    public static readonly RateLimitRule AppDownload = new("app-download", 20, Minute);

    public static RateLimitRule? Match(string method, PathString path)
    {
        var post = HttpMethods.IsPost(method);
        if (post && (path.Equals("/login", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/mobile/v1/auth/login", StringComparison.OrdinalIgnoreCase))) return Login;
        if (post && path.Equals(TwoFactorLoginFlow.PagePath, StringComparison.OrdinalIgnoreCase)) return TwoFactor;
        if (post && path.Equals("/auth/clerk/session", StringComparison.OrdinalIgnoreCase)) return ClerkSession;
        if (post && path.Equals("/account/sign-out-everywhere", StringComparison.OrdinalIgnoreCase)) return SignOutEverywhere;
        if (post && path.Equals(SecurityHeaderPolicy.CspReportPath, StringComparison.OrdinalIgnoreCase)) return CspReport;
        if (path.Equals("/download/android", StringComparison.OrdinalIgnoreCase)) return AppDownload;
        if (path.StartsWithSegments("/auth/external", StringComparison.OrdinalIgnoreCase)) return ExternalSignIn;
        if (path.Equals("/register", StringComparison.OrdinalIgnoreCase) || path.Equals("/forgot-password", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/reset-password", StringComparison.OrdinalIgnoreCase)) return AccountPages;
        return null;
    }

    /// <summary>
    /// The client IP, read the same way Program.cs's forwarded-headers setup reads it (the last X-Forwarded-For hop, else the
    /// socket address). This runs before that middleware, so it repeats the logic rather than relying on it. As Program.cs notes,
    /// the app port must only be reachable through the proxy - otherwise a client could vary X-Forwarded-For.
    /// </summary>
    public static string ClientKey(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(last)) return last;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static PartitionedRateLimiter<HttpContext> CreateLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var rule = Match(context.Request.Method, context.Request.Path);
            if (rule is null) return RateLimitPartition.GetNoLimiter("none");
            return RateLimitPartition.GetFixedWindowLimiter(rule.Name + "|" + ClientKey(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
}
