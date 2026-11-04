using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiPulse.Services;

/// <summary>Who Clerk says a verified session token belongs to.</summary>
public sealed record ClerkIdentity(string Subject, string? Email, bool EmailVerified, string? Name);

public sealed record ClerkValidation(ClerkIdentity? Identity, string? Error)
{
    public bool Ok => Identity is not null;
    public static ClerkValidation Fail(string error) => new(null, error);
}

/// <summary>
/// A Clerk publishable key (<c>pk_test_...</c> / <c>pk_live_...</c>) is not secret and carries the Frontend API host, base64 encoded and
/// terminated with "$". Reading it from the key means an admin only has to paste one value, and the issuer to trust can never be
/// mistyped separately from the key.
/// </summary>
public static class ClerkKey
{
    public static bool TryGetFrontendHost(string? publishableKey, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(publishableKey)) return false;
        var parts = publishableKey.Trim().Split('_', 3);
        if (parts.Length != 3 || parts[0] != "pk" || (parts[1] != "test" && parts[1] != "live")) return false;

        try
        {
            var padded = parts[2].PadRight(parts[2].Length + (4 - parts[2].Length % 4) % 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (!decoded.EndsWith('$')) return false;
            var candidate = decoded[..^1];
            // A host name and nothing else: this value is placed in URLs and compared against the token issuer.
            if (candidate.Length < 4 || candidate.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) || !candidate.Contains('.')) return false;
            host = candidate.ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Issuer(string frontendHost) => "https://" + frontendHost;
}

/// <summary>
/// Verifies a Clerk session token (a JWT signed RS256 with the instance's private key) against the instance's public keys, with no call
/// to Clerk's secret API, so no secret key is ever stored in AiPulse. Written against the JWT rules directly rather than trusting
/// whatever the token claims about itself: only RS256 is accepted (so "alg: none" and HS256-with-the-public-key tricks are refused), the
/// signing key must come from the instance's own JWKS, and issuer, expiry, not-before and authorized party are all checked.
/// </summary>
public sealed class ClerkTokenValidator
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeysFreshFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefetchGap = TimeSpan.FromMinutes(2);

    private readonly Func<string, CancellationToken, Task<string>> _fetchJson;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (Dictionary<string, RSAParameters> Keys, DateTimeOffset FetchedAt)> _cache = new();

    public ClerkTokenValidator(Func<string, CancellationToken, Task<string>> fetchJson, TimeProvider? time = null)
    {
        _fetchJson = fetchJson;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="expectedOrigin">The site's own origin (e.g. https://aipulse.example). Clerk stamps the page that asked for the token into
    /// <c>azp</c>; a token minted for some other site is refused. Null skips the check.</param>
    public async Task<ClerkValidation> ValidateAsync(string? token, string frontendHost, string? expectedOrigin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return ClerkValidation.Fail("No token.");
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return ClerkValidation.Fail("Not a JWT.");

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url(parts[0])).RootElement.Clone();
            payload = JsonDocument.Parse(Base64Url(parts[1])).RootElement.Clone();
            signature = Base64Url(parts[2]);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return ClerkValidation.Fail("Malformed token.");
        }

        if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object) return ClerkValidation.Fail("Malformed token.");
        if (Str(header, "alg") != "RS256") return ClerkValidation.Fail("Unsupported algorithm.");
        var kid = Str(header, "kid");
        if (string.IsNullOrEmpty(kid)) return ClerkValidation.Fail("No key id.");

        var key = await FindKeyAsync(frontendHost, kid, ct);
        if (key is null) return ClerkValidation.Fail("Unknown signing key.");

        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key.Value);
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return ClerkValidation.Fail("Bad signature.");
        }

        var now = _time.GetUtcNow();
        if (Str(payload, "iss") != ClerkKey.Issuer(frontendHost)) return ClerkValidation.Fail("Wrong issuer.");
        if (!Num(payload, "exp", out var exp) || now > DateTimeOffset.FromUnixTimeSeconds(exp) + Skew) return ClerkValidation.Fail("Token expired.");
        if (Num(payload, "nbf", out var nbf) && now + Skew < DateTimeOffset.FromUnixTimeSeconds(nbf)) return ClerkValidation.Fail("Token not valid yet.");

        var azp = Str(payload, "azp");
        if (expectedOrigin is not null && !string.IsNullOrEmpty(azp) && !string.Equals(azp.TrimEnd('/'), expectedOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return ClerkValidation.Fail("Token was issued for a different site.");

        var sub = Str(payload, "sub");
        if (string.IsNullOrEmpty(sub)) return ClerkValidation.Fail("No subject.");

        var name = Str(payload, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = string.Join(' ', new[] { Str(payload, "first_name"), Str(payload, "last_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new ClerkValidation(new ClerkIdentity(sub, Str(payload, "email"), Bool(payload, "email_verified"), string.IsNullOrWhiteSpace(name) ? null : name), null);
    }

    private async Task<RSAParameters?> FindKeyAsync(string host, string kid, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(host, out var cached))
            {
                if (cached.Keys.TryGetValue(kid, out var hit) && now - cached.FetchedAt < KeysFreshFor) return hit;
                // A key id we have never seen usually means Clerk rotated keys; look once, but not on every bogus token.
                if (cached.Keys.ContainsKey(kid) is false && now - cached.FetchedAt < MinRefetchGap) return null;
            }

            var keys = ParseJwks(await _fetchJson($"https://{host}/.well-known/jwks.json", ct));
            _cache[host] = (keys, now);
            return keys.TryGetValue(kid, out var found) ? found : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, RSAParameters> ParseJwks(string json)
    {
        var keys = new Dictionary<string, RSAParameters>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array) return keys;
        foreach (var k in list.EnumerateArray())
        {
            if (Str(k, "kty") != "RSA") continue;
            var id = Str(k, "kid");
            var n = Str(k, "n");
            var e = Str(k, "e");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(n) || string.IsNullOrEmpty(e)) continue;
            keys[id] = new RSAParameters { Modulus = Base64Url(n), Exponent = Base64Url(e) };
        }
        return keys;
    }

    private static byte[] Base64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Num(JsonElement e, string name, out long value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }

    /// <summary>Clerk session-token templates often emit booleans as the strings "true"/"false"; accept both, default to not verified.</summary>
    private static bool Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

/// <summary>Body of the browser's hand-over of a freshly minted Clerk session token (see js/clerk-login.js).</summary>
public sealed record ClerkSessionRequest(string? Token, bool Remember, string? ReturnUrl);

/// <summary>
/// Optional Clerk sign-in, switched on and off by an admin in Settings (the same stored row the Google/GitHub switches use, with the
/// publishable key in the client-id column; there is no secret). Local username/password sign-in always keeps working beside it.
/// </summary>
public sealed class ClerkAuthService
{
    public const string Provider = "Clerk";

    private readonly OAuthSettingsService _settings;
    private readonly ClerkTokenValidator _validator;
    private readonly ExternalAuthService _external;

    public ClerkAuthService(OAuthSettingsService settings, ClerkTokenValidator validator, ExternalAuthService external)
    {
        _settings = settings;
        _validator = validator;
        _external = external;
    }

    /// <summary>The Frontend API host when the admin has turned Clerk on and pasted a valid publishable key; null otherwise.</summary>
    public async Task<(string PublishableKey, string Host)?> GetActiveAsync()
    {
        var row = await _settings.GetAsync(Provider);
        if (row is not { Enabled: true } || !ClerkKey.TryGetFrontendHost(row.ClientId, out var host)) return null;
        return (row.ClientId.Trim(), host);
    }

    public async Task<(ExternalLoginResult Result, Models.AppUser? User, string? Error)> SignInAsync(string? token, string origin, CancellationToken ct = default)
    {
        var active = await GetActiveAsync();
        if (active is null) return (ExternalLoginResult.Disabled, null, "Clerk sign-in is not enabled.");

        var checkedToken = await _validator.ValidateAsync(token, active.Value.Host, origin, ct);
        if (checkedToken.Identity is not { } who) return (ExternalLoginResult.Disabled, null, checkedToken.Error);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, who.Subject),
            new Claim(ClaimTypes.Email, who.Email ?? ""),
            new Claim(ClaimTypes.Name, who.Name ?? "")
        }.Where(c => c.Value.Length > 0), "Clerk"));

        var (result, user) = await _external.CompleteAsync(Provider, principal, emailVerified: who.EmailVerified);
        return (result, user, null);
    }
}
