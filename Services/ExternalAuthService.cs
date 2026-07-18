using System.Security.Claims;
using AiPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services;

public enum ExternalLoginResult { Success, PendingApproval, Disabled }

/// <summary>
/// Handles the AiPulse side of a completed Google/GitHub OAuth handshake: find-or-create the local
/// AppUser, link the external identity, and hand back who to sign in. The actual SignInAsync/redirect
/// happens in Program.cs's OnTicketReceived handler, keeping HttpContext ceremony out of this service -
/// same split as UserService.TryLoginAsync/Login.razor.
/// </summary>
public sealed class ExternalAuthService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly UserService _users;

    public ExternalAuthService(IDbContextFactory<AiPulseDbContext> dbFactory, UserService users)
    {
        _dbFactory = dbFactory;
        _users = users;
    }

    public async Task<(ExternalLoginResult Result, AppUser? User)> CompleteAsync(string provider, ClaimsPrincipal externalPrincipal)
    {
        var providerKey = externalPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(providerKey))
            throw new InvalidOperationException($"{provider} did not return a subject id.");

        var email = externalPrincipal.FindFirstValue(ClaimTypes.Email);
        var displayName = externalPrincipal.FindFirstValue(ClaimTypes.Name);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var link = await db.ExternalLogins.FirstOrDefaultAsync(l => l.Provider == provider && l.ProviderKey == providerKey);

        var user = link is not null
            ? await db.Users.FindAsync(link.UserId)
            : null;

        // No link yet, but the provider's email matches an existing password account - link rather than duplicate.
        if (user is null && !string.IsNullOrEmpty(email))
            user = await db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower());

        var isNewUser = false;
        if (user is null)
        {
            isNewUser = true;
            user = new AppUser
            {
                Username = await GenerateUniqueUsernameAsync(db, displayName ?? email ?? $"{provider.ToLower()}user"),
                PasswordHash = "", // OAuth-only account - PasswordHasher.Verify will never match an empty hash
                Email = email,
                EmailVerified = provider == OAuthProviders.Google && email is not null,
                Role = UserRoles.User,
                Status = _users.RequireApproval ? UserStatuses.Pending : UserStatuses.Approved
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        if (link is null)
        {
            db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = provider, ProviderKey = providerKey, Email = email });
            await db.SaveChangesAsync();
        }

        await LogAuditAsync(db, isNewUser ? "User.RegisteredViaOAuth" : "Login.SuccessViaOAuth", user, detail: provider);

        if (user.Status == UserStatuses.Pending) return (ExternalLoginResult.PendingApproval, null);
        if (user.Status == UserStatuses.Disabled) return (ExternalLoginResult.Disabled, null);

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return (ExternalLoginResult.Success, user);
    }

    private static async Task<string> GenerateUniqueUsernameAsync(AiPulseDbContext db, string seed)
    {
        var baseName = new string(seed.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (baseName.Length < 3) baseName = "user" + baseName; // RegisterAsync's own minimum
        if (baseName.Length > 24) baseName = baseName[..24];

        var candidate = baseName;
        var suffix = 1;
        while (await db.Users.AnyAsync(u => u.Username.ToLower() == candidate))
        {
            suffix++;
            candidate = $"{baseName}{suffix}";
        }
        return candidate;
    }

    private static async Task LogAuditAsync(AiPulseDbContext db, string action, AppUser targetUser, string? detail = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Action = action,
            TargetUserId = targetUser.Id,
            TargetUsername = targetUser.Username,
            Detail = detail
        });
        await db.SaveChangesAsync();
    }
}
