using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace AiPulse.Services;

/// <summary>Login settings, bound from the "Auth" section of appsettings.json.</summary>
public sealed class AuthOptions
{
    public string Username { get; set; } = "admin";

    /// <summary>Plaintext password — convenient for first run. Prefer PasswordHash for real security.</summary>
    public string? Password { get; set; }

    /// <summary>PBKDF2 hash (format: v1.iterations.saltBase64.hashBase64). Takes precedence over Password.</summary>
    public string? PasswordHash { get; set; }
}

/// <summary>Validates credentials and builds the signed-in user principal. No external dependencies.</summary>
public sealed class AuthService
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly AuthOptions _opt;
    public AuthService(IOptions<AuthOptions> opt) => _opt = opt.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.Username) &&
        (!string.IsNullOrWhiteSpace(_opt.PasswordHash) || !string.IsNullOrWhiteSpace(_opt.Password));

    /// <summary>Returns a ClaimsPrincipal on success, or null if the credentials are wrong.</summary>
    public ClaimsPrincipal? Validate(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || password is null)
            return null;
        if (!string.Equals(username, _opt.Username, StringComparison.OrdinalIgnoreCase))
            return null;

        bool ok = !string.IsNullOrWhiteSpace(_opt.PasswordHash)
            ? VerifyHash(password, _opt.PasswordHash!)
            : FixedTimeEquals(password, _opt.Password ?? "");

        if (!ok)
            return null;

        var claims = new[] { new Claim(ClaimTypes.Name, _opt.Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>Create a PBKDF2 hash string to paste into appsettings (used by the dev /auth/hash helper).</summary>
    public static string CreateHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyHash(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1")
            return false;
        if (!int.TryParse(parts[1], out var iterations))
            return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
