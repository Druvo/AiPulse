using AiPulse.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiPulse.Services;

public enum LoginResult { Success, InvalidCredentials, PendingApproval, Disabled, LockedOut }

/// <summary>
/// User accounts: registration, admin approval, roles, and login validation. Same EnsureCreated caveat as
/// other DB-backed services here - existing databases don't automatically gain new tables, so the Users
/// table is reconciled with a one-time raw-SQL check on first use.
/// </summary>
public sealed class UserService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly AuthOptions _bootstrap;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<UserService> _log;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    /// <summary>Whether self-registration (username/password or, later, OAuth) needs Admin approval before signing in.</summary>
    public bool RequireApproval => _bootstrap.RequireApproval;

    public UserService(IDbContextFactory<AiPulseDbContext> dbFactory, IOptions<AuthOptions> bootstrap, IEmailSender emailSender, ILogger<UserService> log)
    {
        _dbFactory = dbFactory;
        _bootstrap = bootstrap.Value;
        _emailSender = emailSender;
        _log = log;
    }

    /// <summary>Call once at startup: creates the Users table if missing, and seeds the first Admin from appsettings if the table is empty.</summary>
    public async Task EnsureSchemaAndBootstrapAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);

        if (await db.Users.AnyAsync())
            return;

        if (string.IsNullOrWhiteSpace(_bootstrap.Username))
        {
            _log.LogWarning("No users exist and no Auth:Username is configured - visit /register to create the first account, then approve it directly in the database.");
            return;
        }

        var hash = !string.IsNullOrWhiteSpace(_bootstrap.PasswordHash)
            ? _bootstrap.PasswordHash!
            : PasswordHasher.Hash(_bootstrap.Password ?? "changeme");

        db.Users.Add(new AppUser
        {
            Username = _bootstrap.Username,
            PasswordHash = hash,
            Role = UserRoles.Admin,
            Status = UserStatuses.Approved
        });
        await db.SaveChangesAsync();
        _log.LogInformation("Bootstrapped first Admin account '{Username}' from appsettings Auth section.", _bootstrap.Username);
    }

    public async Task<(LoginResult Result, AppUser? User)> TryLoginAsync(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null)
            return (LoginResult.InvalidCredentials, null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.Trim().ToLower());
        if (user is null)
            return (LoginResult.InvalidCredentials, null);

        var now = DateTimeOffset.UtcNow;
        if (user.LockoutUntil is { } until && until > now)
        {
            await LogAuditAsync(db, "Login.BlockedByLockout", targetUser: user);
            return (LoginResult.LockedOut, null);
        }

        // Empty PasswordHash means an OAuth-only account (Phase 3) - local login can never succeed for it.
        if (string.IsNullOrEmpty(user.PasswordHash) || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            var lockedOutNow = user.FailedLoginCount >= MaxFailedAttempts;
            if (lockedOutNow)
            {
                user.LockoutUntil = now + LockoutDuration;
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync();
            await LogAuditAsync(db, lockedOutNow ? "Login.Lockout" : "Login.Failed", targetUser: user);
            return (lockedOutNow ? LoginResult.LockedOut : LoginResult.InvalidCredentials, null);
        }

        if (user.Status != UserStatuses.Approved)
        {
            await LogAuditAsync(db, user.Status == UserStatuses.Pending ? "Login.BlockedByPending" : "Login.BlockedByDisabled", targetUser: user);
            return (user.Status == UserStatuses.Pending ? LoginResult.PendingApproval : LoginResult.Disabled, null);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "Login.Success", targetUser: user);
        return (LoginResult.Success, user);
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(string username, string password, string? email = null)
    {
        username = username.Trim();
        if (username.Length < 3)
            return (false, "Username must be at least 3 characters.");
        if (password.Length < 6)
            return (false, "Password must be at least 6 characters.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
            return (false, "That username is already taken.");

        var user = new AppUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            Role = UserRoles.User,
            Status = RequireApproval ? UserStatuses.Pending : UserStatuses.Approved
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.Registered", targetUser: user, detail: RequireApproval ? "Pending approval" : "Auto-approved");
        return (true, null);
    }

    public async Task<List<AppUser>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<AppUser> CreateApprovedUserAsync(string username, string password, string role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = new AppUser
        {
            Username = username.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            Status = UserStatuses.Approved
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Admin-initiated password reset - sets a new password directly, no knowledge of the old one required. Bumps the security stamp so any session signed in under the old password is logged out.</summary>
    public async Task<(bool Success, string? Error)> SetPasswordAsync(int id, string newPassword)
    {
        if (newPassword.Length < 6)
            return (false, "Password must be at least 6 characters.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return (false, "User not found.");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.PasswordReset", targetUser: user);
        return (true, null);
    }

    /// <summary>Self-service password change - requires the current password to guard against a hijacked/left-open session.</summary>
    public async Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(string username, string currentPassword, string newPassword)
    {
        if (newPassword.Length < 6)
            return (false, "New password must be at least 6 characters.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user is null) return (false, "User not found.");
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return (false, "Current password is incorrect.");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>
    /// Renames a user's login username. Also migrates their ChatSessions rows (keyed by username) so
    /// Playground history isn't orphaned - the caller is responsible for migrating the per-user App_Data
    /// folder (ReadingStateService.RenameUserFolder), which lives outside the DB.
    /// </summary>
    public async Task<(bool Success, string? Error, string? OldUsername)> RenameUserAsync(int id, string newUsername)
    {
        newUsername = newUsername.Trim();
        if (newUsername.Length < 3)
            return (false, "Username must be at least 3 characters.", null);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return (false, "User not found.", null);

        if (string.Equals(user.Username, newUsername, StringComparison.OrdinalIgnoreCase))
            return (true, null, user.Username);

        if (await db.Users.AnyAsync(u => u.Id != id && u.Username.ToLower() == newUsername.ToLower()))
            return (false, "That username is already taken.", null);

        var oldUsername = user.Username;
        user.Username = newUsername;

        var sessions = await db.ChatSessions.Where(s => s.Username == oldUsername).ToListAsync();
        foreach (var s in sessions) s.Username = newUsername;

        await db.SaveChangesAsync();
        return (true, null, oldUsername);
    }

    public async Task SetStatusAsync(int id, string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return;
        user.Status = status;
        // Disabling should kill any live session too, not just block future logins.
        if (status == UserStatuses.Disabled)
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        await LogAuditAsync(db, status == UserStatuses.Approved ? "User.Approved" : "User.Disabled", targetUser: user);
    }

    public async Task SetRoleAsync(int id, string role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return;
        var oldRole = user.Role;
        user.Role = role;
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.RoleChanged", targetUser: user, detail: $"{oldRole} -> {role}");
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return;
        await LogAuditAsync(db, "User.Deleted", targetUser: user);
        db.Users.Remove(user);
        await db.SaveChangesAsync();
    }

    /// <summary>How many approved Admins currently exist - used to stop the last admin from being locked out.</summary>
    public async Task<int> CountActiveAdminsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users.CountAsync(u => u.Role == UserRoles.Admin && u.Status == UserStatuses.Approved);
    }

    public async Task<AppUser?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users.FindAsync(id);
    }

    /// <summary>Clears a lockout early - the admin-facing escape hatch for a legitimately locked-out user, next to the automatic 15-minute expiry.</summary>
    public async Task ClearLockoutAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return;
        user.LockoutUntil = null;
        user.FailedLoginCount = 0;
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.LockoutCleared", targetUser: user);
    }

    /// <summary>Invalidates every active session for this account - the "Force logout" admin action. Takes effect on the target's next request/circuit reconnect (see Program.cs's OnValidatePrincipal), not instantly inside an already-open Blazor Server circuit.</summary>
    public async Task BumpSecurityStampAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is null) return;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.ForceLogout", targetUser: user);
    }

    public async Task<(List<AuditLogEntry> Rows, int Total)> GetAuditLogAsync(int page, int pageSize)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var total = await db.AuditLog.CountAsync();
        var rows = await db.AuditLog
            .OrderByDescending(a => a.Id) // SQLite can't translate ORDER BY on a DateTimeOffset column - Id tracks insertion order anyway
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (rows, total);
    }

    /// <summary>
    /// Generates a reset token and emails it (via IEmailSender - a no-op logger when no SMTP is configured,
    /// so the link still appears in the server log). Always returns without revealing whether the email
    /// actually matched an account, to avoid leaking which emails are registered.
    /// </summary>
    public async Task RequestPasswordResetAsync(string email, Func<string, string> buildResetUrl)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.Trim().ToLower());
        if (user is null) return;

        var (token, url) = await CreateResetTokenAsync(db, user, buildResetUrl);
        await _emailSender.SendAsync(email, "Reset your AiPulse password",
            $"Someone requested a password reset for your AiPulse account.\n\nReset it here (valid 1 hour): {url}\n\nIf this wasn't you, ignore this email.");
    }

    /// <summary>Admin escape hatch when no SMTP is configured at all - returns the raw URL directly so Users.razor can show it inline for the admin to copy and hand to the user.</summary>
    public async Task<string> GenerateResetLinkForAdminAsync(int userId, Func<string, string> buildResetUrl)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("User not found.");
        var (_, url) = await CreateResetTokenAsync(db, user, buildResetUrl);
        await LogAuditAsync(db, "User.ResetLinkGenerated", targetUser: user);
        return url;
    }

    private static async Task<(string Token, string Url)> CreateResetTokenAsync(AiPulseDbContext db, AppUser user, Func<string, string> buildResetUrl)
    {
        var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var tokenHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(tokenBytes));

        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow + ResetTokenLifetime
        });
        await db.SaveChangesAsync();
        return (token, buildResetUrl(token));
    }

    public async Task<(bool Success, string? Error)> ResetPasswordWithTokenAsync(string token, string newPassword)
    {
        if (newPassword.Length < 6)
            return (false, "Password must be at least 6 characters.");

        byte[] tokenBytes;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            tokenBytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return (false, "That reset link is invalid.");
        }
        var tokenHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(tokenBytes));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        if (reset is null || reset.UsedAt is not null || reset.ExpiresAt < DateTimeOffset.UtcNow)
            return (false, "That reset link is invalid or has expired - request a new one.");

        var user = await db.Users.FindAsync(reset.UserId);
        if (user is null) return (false, "That reset link is invalid.");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        reset.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await LogAuditAsync(db, "User.PasswordResetViaToken", targetUser: user);
        return (true, null);
    }

    private static async Task LogAuditAsync(AiPulseDbContext db, string action, AppUser? targetUser = null, AppUser? actorUser = null, string? detail = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Action = action,
            ActorUserId = actorUser?.Id,
            ActorUsername = actorUser?.Username,
            TargetUserId = targetUser?.Id,
            TargetUsername = targetUser?.Username,
            Detail = detail
        });
        await db.SaveChangesAsync();
    }

    private async Task EnsureSchemaAsync(AiPulseDbContext db)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaEnsured) return;

            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Users','AuditLog','PasswordResetTokens','OAuthProviderSettings','ExternalLogins')";
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await check.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    existingTables.Add(reader.GetString(0));
            }

            if (!existingTables.Contains("Users"))
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "Users" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                        "Username" TEXT NOT NULL,
                        "PasswordHash" TEXT NOT NULL,
                        "Role" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "Email" TEXT NULL,
                        "EmailVerified" INTEGER NOT NULL DEFAULT 0,
                        "SecurityStamp" TEXT NOT NULL DEFAULT '',
                        "LastLoginAt" TEXT NULL,
                        "FailedLoginCount" INTEGER NOT NULL DEFAULT 0,
                        "LockoutUntil" TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }
            else
            {
                // Installs that already had a Users table before these columns existed won't gain them just
                // because AppUser did - add each directly if missing (same pattern as
                // KnowledgeBaseService.EnsureSourceColumnsAsync).
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA table_info(Users)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await pragma.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                        existingCols.Add(reader.GetString(reader.GetOrdinal("name")));
                }

                var newColumns = new (string Name, string Def)[]
                {
                    ("Email", "TEXT NULL"),
                    ("EmailVerified", "INTEGER NOT NULL DEFAULT 0"),
                    ("SecurityStamp", "TEXT NOT NULL DEFAULT ''"),
                    ("LastLoginAt", "TEXT NULL"),
                    ("FailedLoginCount", "INTEGER NOT NULL DEFAULT 0"),
                    ("LockoutUntil", "TEXT NULL"),
                };
                foreach (var (name, def) in newColumns)
                {
                    if (existingCols.Contains(name)) continue;
                    await using var alter = conn.CreateCommand();
                    alter.CommandText = $"""ALTER TABLE "Users" ADD COLUMN "{name}" {def}""";
                    await alter.ExecuteNonQueryAsync();
                }

                if (!existingCols.Contains("SecurityStamp"))
                {
                    // Every pre-existing row just got the same '' default - give each a real, distinct stamp
                    // rather than leaving them all sharing one value.
                    await using var backfillCmd = conn.CreateCommand();
                    backfillCmd.CommandText = """SELECT "Id" FROM "Users" WHERE "SecurityStamp" = ''""";
                    var ids = new List<long>();
                    await using (var reader = await backfillCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
                    }
                    foreach (var rowId in ids)
                    {
                        await using var update = conn.CreateCommand();
                        update.CommandText = """UPDATE "Users" SET "SecurityStamp" = @stamp WHERE "Id" = @id""";
                        update.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@stamp", Guid.NewGuid().ToString("N")));
                        update.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", rowId));
                        await update.ExecuteNonQueryAsync();
                    }
                }
            }

            if (!existingTables.Contains("AuditLog"))
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "AuditLog" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditLog" PRIMARY KEY AUTOINCREMENT,
                        "TimestampUtc" TEXT NOT NULL,
                        "Action" TEXT NOT NULL,
                        "ActorUserId" INTEGER NULL,
                        "ActorUsername" TEXT NULL,
                        "TargetUserId" INTEGER NULL,
                        "TargetUsername" TEXT NULL,
                        "Detail" TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            if (!existingTables.Contains("PasswordResetTokens"))
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "PasswordResetTokens" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_PasswordResetTokens" PRIMARY KEY AUTOINCREMENT,
                        "UserId" INTEGER NOT NULL,
                        "TokenHash" TEXT NOT NULL,
                        "ExpiresAt" TEXT NOT NULL,
                        "UsedAt" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            if (!existingTables.Contains("OAuthProviderSettings"))
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "OAuthProviderSettings" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_OAuthProviderSettings" PRIMARY KEY AUTOINCREMENT,
                        "Provider" TEXT NOT NULL,
                        "Enabled" INTEGER NOT NULL DEFAULT 0,
                        "ClientId" TEXT NOT NULL DEFAULT '',
                        "ClientSecret" TEXT NOT NULL DEFAULT '',
                        "UpdatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                foreach (var provider in new[] { OAuthProviders.Google, OAuthProviders.GitHub })
                {
                    await using var seed = conn.CreateCommand();
                    seed.CommandText = """INSERT INTO "OAuthProviderSettings" ("Provider", "Enabled", "ClientId", "ClientSecret", "UpdatedAt") VALUES (@provider, 0, '', '', @now)""";
                    seed.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@provider", provider));
                    seed.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                    await seed.ExecuteNonQueryAsync();
                }
            }

            if (!existingTables.Contains("ExternalLogins"))
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "ExternalLogins" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ExternalLogins" PRIMARY KEY AUTOINCREMENT,
                        "UserId" INTEGER NOT NULL,
                        "Provider" TEXT NOT NULL,
                        "ProviderKey" TEXT NOT NULL,
                        "Email" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                await using var index = conn.CreateCommand();
                index.CommandText = """CREATE UNIQUE INDEX "IX_ExternalLogins_Provider_ProviderKey" ON "ExternalLogins" ("Provider", "ProviderKey")""";
                await index.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
