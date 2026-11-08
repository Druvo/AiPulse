using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Security;

/// <summary>Settings for the "Security" configuration section. Every value has a safe default; nothing needs configuring.</summary>
public sealed class SecurityHardeningOptions
{
    /// <summary>Send the full Content-Security-Policy as Report-Only (the default) or enforce it. See docs/SECURITY.md before flipping.</summary>
    public bool CspReportOnly { get; set; } = true;
    public bool CspEnabled { get; set; } = true;
    /// <summary>Extra origins allowed to frame AiPulse (e.g. "https://intranet.example.com"). Empty = only AiPulse itself.</summary>
    public string[] FrameAncestors { get; set; } = Array.Empty<string>();
    public int HstsMaxAgeDays { get; set; } = 180;
    public bool RateLimitingEnabled { get; set; } = true;
    /// <summary>Sign-in endpoints of external providers, which bring their own second factor and never get the TOTP step.</summary>
    public string[] TwoFactorExemptSignInPaths { get; set; } = { "/auth/clerk/session", "/signin-google", "/signin-github" };
}

/// <summary>Builds the header values - pure functions so the tests can pin them down.</summary>
public static class SecurityHeaderPolicy
{
    public const string CspReportPath = "/csp-report";

    /// <summary>Features nothing in AiPulse uses. Fullscreen/autoplay/encrypted-media/picture-in-picture are left alone so
    /// video embeds in Dashboard widgets keep working.</summary>
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), magnetometer=(), gyroscope=(), accelerometer=()";

    public static readonly string[] ClerkSources = { "https://*.clerk.accounts.dev", "https://*.clerk.com", "https://challenges.cloudflare.com" };

    public static string FrameAncestors(IEnumerable<string> extra) =>
        "frame-ancestors " + string.Join(' ', new[] { "'self'" }.Concat(extra.Where(IsSafeSource)));

    /// <summary>
    /// The policy for HTML pages. Shaped by Blazor Server: blazor.web.js from 'self', the SignalR websocket back to this host,
    /// inline style attributes, and App.razor's inline theme script ('unsafe-inline' for scripts until that script moves to a
    /// file or gets a hash - the main thing to fix before enforcing). Feed images and Dashboard widget iframes are arbitrary
    /// http(s) hosts by design. frame-src only controls what AiPulse may embed; who may embed AiPulse is frame-ancestors.
    /// </summary>
    public static string ContentSecurityPolicy(string host, bool https, string? clerkHost, IEnumerable<string> extraFrameAncestors)
    {
        var clerk = clerkHost is null ? "" : " " + string.Join(' ', new[] { "https://" + clerkHost }.Concat(ClerkSources).Distinct());
        var socket = (https ? "wss://" : "ws://") + host;
        return string.Join("; ", new[]
        {
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "form-action 'self'",
            FrameAncestors(extraFrameAncestors),
            $"script-src 'self' 'unsafe-inline'{clerk}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob: https: http:",
            "font-src 'self' data:",
            $"connect-src 'self' {socket}{clerk}",
            "media-src 'self' blob: https: http:",
            "frame-src 'self' https: http:",
            "worker-src 'self' blob:",
            "manifest-src 'self'",
            $"report-uri {CspReportPath}"
        });
    }

    /// <summary>Only plain origins/keywords make it into a header - a stray ';' in config must not inject a directive.</summary>
    internal static bool IsSafeSource(string source) =>
        !string.IsNullOrWhiteSpace(source) && source.All(c => c > ' ' && c < 127 && c != ';' && c != ',' && c != '"');

    public static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1" || host == "[::1]" || host == "::1";
}

/// <summary>
/// Adds the security headers to every response, at the moment headers are sent (Response.OnStarting) - by then the
/// forwarded-headers middleware in Program.cs has run, so Request.IsHttps reflects what the visitor used even behind a
/// TLS-terminating proxy. Headers an endpoint already set are left alone.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHardeningOptions _options;
    private readonly ClerkCspHostSource _clerk;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHardeningOptions> options, ClerkCspHostSource clerk)
    {
        _next = next;
        _options = options.Value;
        _clerk = clerk;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() => ApplyAsync(context));
        return _next(context);
    }

    private async Task ApplyAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.TryAdd("Permissions-Policy", SecurityHeaderPolicy.PermissionsPolicy);

        var extraAncestors = _options.FrameAncestors.Where(SecurityHeaderPolicy.IsSafeSource).ToArray();
        // X-Frame-Options cannot list origins, so it is only sent when AiPulse alone may frame itself; older browsers then
        // still get the protection, newer ones follow frame-ancestors.
        if (extraAncestors.Length == 0) headers.TryAdd("X-Frame-Options", "SAMEORIGIN");

        var request = context.Request;
        var host = request.Host.Host ?? "";
        if (request.IsHttps && !SecurityHeaderPolicy.IsLocalHost(host) && _options.HstsMaxAgeDays > 0)
            headers.TryAdd("Strict-Transport-Security", $"max-age={_options.HstsMaxAgeDays * 86400}");

        var isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (_options.CspEnabled && isHtml)
        {
            var policy = SecurityHeaderPolicy.ContentSecurityPolicy(request.Host.Value ?? host, request.IsHttps, await _clerk.GetHostAsync(), extraAncestors);
            if (_options.CspReportOnly)
            {
                headers.TryAdd("Content-Security-Policy-Report-Only", policy);
                // frame-ancestors is ignored in a report-only policy, so clickjacking protection is enforced on its own.
                headers.TryAdd("Content-Security-Policy", SecurityHeaderPolicy.FrameAncestors(extraAncestors));
            }
            else
            {
                headers.TryAdd("Content-Security-Policy", policy);
            }
        }
        else if (!headers.ContainsKey("Content-Security-Policy"))
        {
            headers["Content-Security-Policy"] = SecurityHeaderPolicy.FrameAncestors(extraAncestors);
        }
    }
}

/// <summary>The Clerk Frontend API host (from the publishable key) when Clerk sign-in is on, cached for a minute.</summary>
public sealed class ClerkCspHostSource
{
    private readonly IServiceProvider _services;
    private (string? Host, DateTimeOffset At) _cached = (null, DateTimeOffset.MinValue);

    public ClerkCspHostSource(IServiceProvider services) => _services = services;

    public async Task<string?> GetHostAsync()
    {
        var cached = _cached;
        if (DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Host;
        string? host = null;
        try
        {
            if (_services.GetService(typeof(ClerkAuthService)) is ClerkAuthService clerk && await clerk.GetActiveAsync() is { } active
                && SecurityHeaderPolicy.IsSafeSource(active.Host))
                host = active.Host;
        }
        catch
        {
            // A settings read failing must never take page rendering down with it.
        }
        _cached = (host, DateTimeOffset.UtcNow);
        return host;
    }
}

public sealed record CspViolation(DateTimeOffset At, string Directive, string BlockedUri, string DocumentUri, string? Source);

/// <summary>The most recent CSP violation reports, in memory, so an admin can see what enforcing would break.</summary>
public sealed class CspViolationLog
{
    private const int Capacity = 100;
    private readonly ConcurrentQueue<CspViolation> _items = new();

    public void Add(CspViolation violation)
    {
        _items.Enqueue(violation);
        while (_items.Count > Capacity && _items.TryDequeue(out _)) { }
    }

    public IReadOnlyList<CspViolation> Recent() => _items.Reverse().ToList();

    /// <summary>Reads both report formats: the classic {"csp-report":{...}} and the Reporting API's [{"type":"csp-violation","body":{...}}].</summary>
    public static List<CspViolation> Parse(string json, DateTimeOffset now)
    {
        var result = new List<CspViolation>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("csp-report", out var classic))
                result.Add(From(classic, "violated-directive", "blocked-uri", "document-uri", "source-file", now));
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var report in root.EnumerateArray().Take(20))
                    if (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
                        result.Add(From(body, "effectiveDirective", "blockedURL", "documentURL", "sourceFile", now));
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static CspViolation From(JsonElement e, string directive, string blocked, string document, string source, DateTimeOffset now)
    {
        string Read(string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString()) : "";
        var src = Read(source);
        return new CspViolation(now, Read(directive), Read(blocked), Read(document), src.Length == 0 ? null : src);
    }

    /// <summary>Report bodies are attacker-controlled: cap the length and strip control characters before they reach a log.</summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, 300));
        foreach (var c in value)
        {
            if (sb.Length >= 300) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}

/// <summary>POST /csp-report: browsers send CSP violation reports here. Anonymous by necessity; body size capped and rate limited.</summary>
public sealed class CspReportMiddleware
{
    private const int MaxBodyBytes = 16 * 1024;
    private readonly RequestDelegate _next;
    private readonly CspViolationLog _log;
    private readonly ILogger<CspReportMiddleware> _logger;

    public CspReportMiddleware(RequestDelegate next, CspViolationLog log, ILogger<CspReportMiddleware> logger)
    {
        _next = next;
        _log = log;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals(SecurityHeaderPolicy.CspReportPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var buffer = new byte[MaxBodyBytes];
        var read = 0;
        int n;
        while (read < MaxBodyBytes && (n = await context.Request.Body.ReadAsync(buffer.AsMemory(read, MaxBodyBytes - read), context.RequestAborted)) > 0)
            read += n;

        foreach (var violation in CspViolationLog.Parse(Encoding.UTF8.GetString(buffer, 0, read), DateTimeOffset.UtcNow))
        {
            _log.Add(violation);
            _logger.LogWarning("CSP violation: {Directive} blocked {BlockedUri} on {DocumentUri} ({Source})",
                violation.Directive, violation.BlockedUri, violation.DocumentUri, violation.Source);
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
