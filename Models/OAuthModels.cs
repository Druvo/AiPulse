namespace AiPulse.Models;

public static class OAuthProviders
{
    public const string Google = "Google";
    public const string GitHub = "GitHub";
}

/// <summary>Admin-configurable per-provider OAuth credentials (Settings.razor's "OAuth Sign-in" admin
/// card), stored in the DB rather than appsettings.json so an admin can enable/disable and rotate
/// credentials without touching config files or restarting AiPulse.</summary>
public sealed class OAuthProviderSettings
{
    public int Id { get; set; }
    public required string Provider { get; set; }
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Links an AppUser to an external identity. Keyed on the provider's own stable subject id
/// (ProviderKey), not email - email can change provider-side and isn't guaranteed unique/verified for
/// every provider (GitHub in particular).</summary>
public sealed class ExternalLogin
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderKey { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
