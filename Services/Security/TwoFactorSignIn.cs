using System.Security.Claims;
using AiPulse.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AiPulse.Services.Security;

/// <summary>Who passed the password step and is now owed a code prompt. Lives only inside the encrypted challenge cookie.</summary>
public sealed record TwoFactorChallenge(int UserId, string SecurityStamp, bool Remember, string ReturnUrl);

public enum TwoFactorCompletionStatus { Success, Invalid, LockedOut, Expired }

public sealed record TwoFactorCompletion(TwoFactorCompletionStatus Status, string? RedirectTo = null, DateTimeOffset? LockedUntil = null, bool UsedRecoveryCode = false, int RecoveryCodesLeft = 0);

/// <summary>
/// The second half of password sign-in for accounts with 2FA on. After the password checks out, no auth cookie is issued;
/// instead a short-lived, Data Protection-encrypted challenge cookie (scoped to /login/2fa) remembers who is mid-sign-in.
/// Only <see cref="CompleteAsync"/>, after a valid code, issues the real AiPulse.Auth cookie - marked with an "amr=mfa" claim.
/// </summary>
public sealed class TwoFactorLoginFlow
{
    public const string ChallengeCookieName = "AiPulse.2fa";
    public const string PagePath = "/login/2fa";
    public const string MfaClaimType = "amr";
    public const string MfaClaimValue = "mfa";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private const string ProtectorPurpose = "AiPulse.TwoFactorChallenge.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TwoFactorService _twoFactor;
    private readonly UserService _users;

    public TwoFactorLoginFlow(IDataProtectionProvider dataProtection, TwoFactorService twoFactor, UserService users)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _twoFactor = twoFactor;
        _users = users;
    }

    public void IssueChallenge(HttpContext http, TwoFactorChallenge challenge)
    {
        var payload = string.Join('\n', challenge.UserId, challenge.SecurityStamp, challenge.Remember ? "1" : "0", challenge.ReturnUrl);
        http.Response.Cookies.Append(ChallengeCookieName, _protector.Protect(payload, ChallengeLifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = PagePath,
            MaxAge = ChallengeLifetime,
            IsEssential = true
        });
    }

    public TwoFactorChallenge? ReadChallenge(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(ChallengeCookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = _protector.Unprotect(raw).Split('\n');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var userId)) return null;
            return new TwoFactorChallenge(userId, parts[1], parts[2] == "1", LocalUrl.Sanitize(parts[3]) ?? "/dashboard");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // expired, tampered with, or from another key ring
        }
    }

    public void ClearChallenge(HttpContext http) =>
        http.Response.Cookies.Delete(ChallengeCookieName, new CookieOptions { Path = PagePath, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, HttpOnly = true });

    public async Task<TwoFactorCompletion> CompleteAsync(HttpContext http, string? code)
    {
        var challenge = ReadChallenge(http);
        if (challenge is null) return new TwoFactorCompletion(TwoFactorCompletionStatus.Expired);

        // A password reset, admin "force logout" or disable since the password step voids the challenge.
        var user = await _users.GetByIdAsync(challenge.UserId);
        if (user is null || user.Status != UserStatuses.Approved || user.SecurityStamp != challenge.SecurityStamp)
        {
            ClearChallenge(http);
            return new TwoFactorCompletion(TwoFactorCompletionStatus.Expired);
        }

        var check = await _twoFactor.VerifyLoginAsync(user.Id, code);
        switch (check.Result)
        {
            case TwoFactorCheck.Success:
                break;
            case TwoFactorCheck.LockedOut:
                return new TwoFactorCompletion(TwoFactorCompletionStatus.LockedOut, LockedUntil: check.LockedUntil);
            case TwoFactorCheck.NotEnabled:
                ClearChallenge(http); // 2FA was reset meanwhile - start over; the password alone will do next time
                return new TwoFactorCompletion(TwoFactorCompletionStatus.Expired);
            default:
                return new TwoFactorCompletion(TwoFactorCompletionStatus.Invalid);
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(user, mfa: true),
            new AuthenticationProperties { IsPersistent = challenge.Remember });
        ClearChallenge(http);
        return new TwoFactorCompletion(TwoFactorCompletionStatus.Success, challenge.ReturnUrl, UsedRecoveryCode: check.UsedRecoveryCode, RecoveryCodesLeft: check.RecoveryCodesLeft);
    }

    /// <summary>The same four claims every AiPulse sign-in carries (see Login.razor / Program.cs), plus amr=mfa when a code was checked.</summary>
    public static ClaimsPrincipal BuildPrincipal(AppUser user, bool mfa)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role),
            new("security_stamp", user.SecurityStamp)
        };
        if (mfa) claims.Add(new Claim(MfaClaimType, MfaClaimValue));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

/// <summary>
/// Wraps the framework's IAuthenticationService so that ANY cookie sign-in for an account with 2FA on - Login.razor's today, or
/// a redesigned login page tomorrow - is held back until the second step, instead of trusting each sign-in page to remember to
/// ask. The deferred request is answered with a redirect to /login/2fa (whatever redirect the page itself issued becomes the
/// return URL). Sign-ins that already carry amr=mfa, and the external-provider endpoints (Clerk, Google, GitHub - those
/// providers run their own second factor), pass straight through.
/// </summary>
public sealed class TwoFactorSignInGuard : IAuthenticationService
{
    internal const string DeferredItemKey = "AiPulse.TwoFactorDeferred";
    private const string EnhancedNavRedirectHeader = "blazor-enhanced-nav-redirect-location";

    private readonly IAuthenticationService _inner;
    private readonly TwoFactorService _twoFactor;
    private readonly TwoFactorLoginFlow _flow;
    private readonly SecurityHardeningOptions _options;

    public TwoFactorSignInGuard(IAuthenticationService inner, TwoFactorService twoFactor, TwoFactorLoginFlow flow, IOptions<SecurityHardeningOptions> options)
    {
        _inner = inner;
        _twoFactor = twoFactor;
        _flow = flow;
        _options = options.Value;
    }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => _inner.AuthenticateAsync(context, scheme);
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => _inner.ChallengeAsync(context, scheme, properties);
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => _inner.ForbidAsync(context, scheme, properties);
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => _inner.SignOutAsync(context, scheme, properties);

    public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        if (await NeedsSecondStepAsync(context, scheme, principal) is not { } userId)
        {
            await _inner.SignInAsync(context, scheme, principal, properties);
            return;
        }

        var stamp = principal.FindFirst("security_stamp")?.Value ?? "";
        var remember = properties?.IsPersistent ?? false;
        var fallbackReturnUrl = LocalUrl.Sanitize(context.Request.Query["ReturnUrl"].FirstOrDefault()) ?? "/dashboard";
        context.Items[DeferredItemKey] = true;
        await _twoFactor.AuditAsync(userId, "Login.TwoFactorRequired");

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var pageRedirect = headers.TryGetValue(EnhancedNavRedirectHeader, out var enhanced) ? enhanced.ToString() : headers.Location.ToString();
            var returnUrl = LocalUrl.FromRedirect(context.Request, pageRedirect) ?? fallbackReturnUrl;
            if (returnUrl.StartsWith(TwoFactorLoginFlow.PagePath, StringComparison.OrdinalIgnoreCase) || returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
                returnUrl = "/dashboard";

            _flow.IssueChallenge(context, new TwoFactorChallenge(userId, stamp, remember, returnUrl));
            if (headers.ContainsKey(EnhancedNavRedirectHeader))
            {
                headers[EnhancedNavRedirectHeader] = TwoFactorLoginFlow.PagePath;
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                headers.Location = TwoFactorLoginFlow.PagePath;
            }
            headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    /// <returns>The user id when this sign-in must wait for a code; null when it can go ahead.</returns>
    private async Task<int?> NeedsSecondStepAsync(HttpContext context, string? scheme, ClaimsPrincipal principal)
    {
        if (scheme is not null && scheme != CookieAuthenticationDefaults.AuthenticationScheme) return null;
        if (principal.HasClaim(TwoFactorLoginFlow.MfaClaimType, TwoFactorLoginFlow.MfaClaimValue)) return null;
        if (!int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return null;

        var path = context.Request.Path;
        foreach (var exempt in _options.TwoFactorExemptSignInPaths)
            if (path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase)) return null;

        return await _twoFactor.IsEnabledAsync(userId) ? userId : null;
    }
}

/// <summary>Open-redirect guard shared by the 2FA flow: only same-site relative paths survive.</summary>
public static class LocalUrl
{
    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.StartsWith('/') || url.StartsWith("//") || url.StartsWith("/\\") || url.Contains('\\')) return null;
        foreach (var c in url) if (char.IsControl(c)) return null;
        return url;
    }

    /// <summary>A page's own redirect (often absolute, e.g. http://host/dashboard from NavigationManager) reduced to a safe local path.</summary>
    public static string? FromRedirect(HttpRequest request, string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        if (Uri.TryCreate(location, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            if (!string.Equals(absolute.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)) return null;
            return Sanitize(absolute.PathAndQuery);
        }
        return Sanitize(location);
    }
}
