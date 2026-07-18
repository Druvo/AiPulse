namespace AiPulse.Models;

/// <summary>Fixed set of roles. Kept as plain strings in the DB for simplicity - these are the only valid values.</summary>
public static class UserRoles
{
    public const string Admin = "Admin";
    /// <summary>Content curation only - can review/approve the Glossary/Tools/Learning Hub/Free AI APIs discovery
    /// queues, but nothing system-level (Sources, Users, Source Health, Playground stay Admin-only).</summary>
    public const string Curator = "Curator";
    public const string User = "User";
}

/// <summary>Account lifecycle: Pending accounts can't sign in until an Admin approves them.</summary>
public static class UserStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Disabled = "Disabled";
}

/// <summary>EF Core-mapped user account (App_Data/aipulse.db).</summary>
public sealed class AppUser
{
    public int Id { get; set; }
    public required string Username { get; set; }
    /// <summary>PBKDF2 hash. Empty string (never null - keeps the SQLite column NOT NULL with no migration
    /// wrinkle) means "no local password set" - an OAuth-only account that must sign in via Google/GitHub.</summary>
    public required string PasswordHash { get; set; }
    public string Role { get; set; } = UserRoles.User;
    public string Status { get; set; } = UserStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    /// <summary>Bumped (a fresh GUID) whenever this account's active sessions should be invalidated - password
    /// reset, an admin's explicit "Force logout," or getting Disabled. Checked against the session's own
    /// security_stamp claim on each request; see Program.cs's OnValidatePrincipal.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    /// <summary>Set once FailedLoginCount hits the threshold; login is blocked until this passes, regardless of password correctness.</summary>
    public DateTimeOffset? LockoutUntil { get; set; }
}

/// <summary>One admin-relevant event (login, lockout, role change, etc.), kept so Users.razor can show real
/// activity history instead of just current state. Actor/Target usernames are denormalized (copied at write
/// time) so the log stays readable after a rename or delete - same reasoning UserService.RenameUserAsync
/// already uses for ChatSessions.</summary>
public sealed class AuditLogEntry
{
    public int Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public required string Action { get; set; }
    public int? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }
    public int? TargetUserId { get; set; }
    public string? TargetUsername { get; set; }
    public string? Detail { get; set; }
}

/// <summary>A pending password-reset request. Stores a hash of the token (same caution as password storage)
/// rather than the raw value - the raw token exists only transiently, at generation time, which is exactly
/// when it needs to be emailed/displayed anyway.</summary>
public sealed class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
