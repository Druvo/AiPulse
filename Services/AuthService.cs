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
}
