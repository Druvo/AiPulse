namespace AiPulse.Services;

/// <summary>
/// Bootstrap login settings, bound from the "Auth" section of appsettings.json. Used only once, at
/// startup, to seed the first Admin account in the Users table if none exist yet - see
/// <see cref="UserService.EnsureSchemaAndBootstrapAsync"/>. Actual login goes through UserService/Users
/// table from then on.
/// </summary>
public sealed class AuthOptions
{
    public string Username { get; set; } = "admin";

    /// <summary>Plaintext password - convenient for first run. Prefer PasswordHash for real security.</summary>
    public string? Password { get; set; }

    /// <summary>PBKDF2 hash (format: v1.iterations.saltBase64.hashBase64). Takes precedence over Password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Whether self-registration (username/password or OAuth) lands as Pending, needing an Admin to approve
    /// before the account can sign in. Defaults to false - open registration, auto-approved - per an explicit
    /// choice made after being shown the risk framing (anyone reaching this instance can create a working
    /// account immediately). Flip to true in appsettings to restore the original approval-gated behavior
    /// without a rebuild.
    /// </summary>
    public bool RequireApproval { get; set; }

    /// <summary>
    /// Email addresses that are always administrators. When someone signs in through a provider that VERIFIES the address (Google, or
    /// Clerk with the email_verified claim) and it is on this list, their account is made an Admin. Set it in configuration or the
    /// environment (<c>Auth__AdminEmails__0=you@example.com</c>); it is how the owner becomes admin on a site whose only account so far
    /// is the bootstrap one. Addresses a provider does not verify never grant a role.
    /// </summary>
    public string[] AdminEmails { get; set; } = Array.Empty<string>();

    public bool IsAdminEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && AdminEmails.Any(a => string.Equals(a?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}
