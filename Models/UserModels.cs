namespace AiPulse.Models;

/// <summary>Fixed set of roles. Kept as plain strings in the DB for simplicity - these are the only valid values.</summary>
public static class UserRoles
{
    public const string Admin = "Admin";
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
    public required string PasswordHash { get; set; }
    public string Role { get; set; } = UserRoles.User;
    public string Status { get; set; } = UserStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
