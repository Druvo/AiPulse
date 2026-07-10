using AiPulse.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiPulse.Services;

public enum LoginResult { Success, InvalidCredentials, PendingApproval, Disabled }

/// <summary>
/// User accounts: registration, admin approval, roles, and login validation. Same EnsureCreated caveat as
/// other DB-backed services here - existing databases don't automatically gain new tables, so the Users
/// table is reconciled with a one-time raw-SQL check on first use.
/// </summary>
public sealed class UserService
{
    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly AuthOptions _bootstrap;
    private readonly ILogger<UserService> _log;
    private bool _schemaEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public UserService(IDbContextFactory<AiPulseDbContext> dbFactory, IOptions<AuthOptions> bootstrap, ILogger<UserService> log)
    {
        _dbFactory = dbFactory;
        _bootstrap = bootstrap.Value;
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
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
            return (LoginResult.InvalidCredentials, null);

        return user.Status switch
        {
            UserStatuses.Approved => (LoginResult.Success, user),
            UserStatuses.Pending => (LoginResult.PendingApproval, null),
            _ => (LoginResult.Disabled, null)
        };
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(string username, string password)
    {
        username = username.Trim();
        if (username.Length < 3)
            return (false, "Username must be at least 3 characters.");
        if (password.Length < 6)
            return (false, "Password must be at least 6 characters.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
            return (false, "That username is already taken.");

        db.Users.Add(new AppUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRoles.User,
            Status = UserStatuses.Pending
        });
        await db.SaveChangesAsync();
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

    public async Task SetStatusAsync(int id, string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is not null) { user.Status = status; await db.SaveChangesAsync(); }
    }

    public async Task SetRoleAsync(int id, string role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is not null) { user.Role = role; await db.SaveChangesAsync(); }
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(id);
        if (user is not null) { db.Users.Remove(user); await db.SaveChangesAsync(); }
    }

    /// <summary>How many approved Admins currently exist - used to stop the last admin from being locked out.</summary>
    public async Task<int> CountActiveAdminsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users.CountAsync(u => u.Role == UserRoles.Admin && u.Status == UserStatuses.Approved);
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
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Users'";
            var exists = await check.ExecuteScalarAsync() is not null;
            if (!exists)
            {
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "Users" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                        "Username" TEXT NOT NULL,
                        "PasswordHash" TEXT NOT NULL,
                        "Role" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
