using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AiPulse.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AiPulse.Services.Security;

public enum TwoFactorCheck { Success, Invalid, LockedOut, NotEnabled }

public sealed record TwoFactorVerification(TwoFactorCheck Result, bool UsedRecoveryCode = false, int RecoveryCodesLeft = 0, DateTimeOffset? LockedUntil = null);

public sealed record TwoFactorStatus(bool Enabled, DateTimeOffset? EnabledAt, int RecoveryCodesLeft, bool EnrollmentPending, DateTimeOffset? LockedUntil);

public sealed record TwoFactorEnrollment(string ManualKey, string OtpAuthUri, string QrSvg);

public sealed record TwoFactorEnrolledUser(int UserId, string Username, DateTimeOffset? EnabledAt, int RecoveryCodesLeft);

/// <summary>
/// TOTP two-factor authentication for local (username + password) accounts: enrolment, sign-in verification with replay
/// protection and per-account throttling, single-use recovery codes, disable and admin reset. Every security-relevant step
/// writes an AuditLog row (the same table Users.razor shows). Google/GitHub/Clerk sign-ins never come through here - those
/// providers run their own second factor.
/// </summary>
public sealed class TwoFactorService
{
    public const string Issuer = "AiPulse";
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PendingEnrollmentLifetime = TimeSpan.FromMinutes(30);
    public const int RecoveryCodeCount = 10;
    private const int RecoveryCodeLength = 10;
    private const string RecoveryAlphabet = "23456789abcdefghjkmnpqrstuvwxyz"; // no 0/o, 1/l/i look-alikes
    private const int RecoveryHashIterations = 10_000; // ~50 bits of entropy per code; PBKDF2 makes offline guessing costly
    internal const string ProtectorPurpose = "AiPulse.TwoFactorSecret.v1";

    private readonly IDbContextFactory<AiPulseDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<TwoFactorService> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;
    // One check at a time per account, so two simultaneous requests cannot both spend the same code.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();

    public TwoFactorService(IDbContextFactory<AiPulseDbContext> dbFactory, IDataProtectionProvider dataProtection, ILogger<TwoFactorService> log, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public async Task<bool> IsEnabledAsync(int userId)
    {
        await using var db = await OpenAsync();
        return await db.UserTwoFactors.AnyAsync(t => t.UserId == userId && t.Enabled);
    }

    public async Task<TwoFactorStatus> GetStatusAsync(int userId)
    {
        await using var db = await OpenAsync();
        var row = await db.UserTwoFactors.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId);
        if (row is null) return new TwoFactorStatus(false, null, 0, false, null);
        var pending = row.PendingSecretProtected is not null && row.PendingCreatedAt is { } created && Now - created < PendingEnrollmentLifetime;
        var locked = row.LockoutUntil is { } until && until > Now ? until : (DateTimeOffset?)null;
        return new TwoFactorStatus(row.Enabled, row.EnabledAt, CountCodes(row.RecoveryCodeHashes), pending, locked);
    }

    /// <summary>Starts (or restarts) enrolment: a fresh secret is stored encrypted as "pending" and returned for the QR code.</summary>
    public async Task<TwoFactorEnrollment> BeginEnrollmentAsync(int userId)
    {
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("User not found.");
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException("Two-factor authentication protects password sign-in; this account signs in through an external provider.");

        var row = await db.UserTwoFactors.FindAsync(userId);
        if (row is { Enabled: true }) throw new InvalidOperationException("Two-factor authentication is already on.");
        if (row is null)
        {
            row = new UserTwoFactor { UserId = userId };
            db.UserTwoFactors.Add(row);
        }

        var secret = Totp.NewSecret();
        row.PendingSecretProtected = _protector.Protect(Base32.Encode(secret));
        row.PendingCreatedAt = Now;
        row.UpdatedAt = Now;
        Audit(db, "TwoFactor.EnrollmentStarted", user);
        await db.SaveChangesAsync();

        var uri = Totp.OtpAuthUri(Issuer, user.Username, secret);
        var svg = QrCode.EncodeText(uri).ToSvg(title: "QR code for your authenticator app");
        return new TwoFactorEnrollment(Totp.FormatForManualEntry(secret), uri, svg);
    }

    public async Task CancelEnrollmentAsync(int userId)
    {
        await using var db = await OpenAsync();
        var row = await db.UserTwoFactors.FindAsync(userId);
        if (row is null || row.Enabled) return;
        db.UserTwoFactors.Remove(row);
        await db.SaveChangesAsync();
    }

    /// <summary>Turns 2FA on once the person proves their app produces the right codes. Returns the one-time recovery codes.</summary>
    public async Task<(bool Ok, string? Error, IReadOnlyList<string>? RecoveryCodes)> ConfirmEnrollmentAsync(int userId, string? code)
    {
        using var _ = await LockUserAsync(userId);
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        var row = await db.UserTwoFactors.FindAsync(userId);
        if (user is null || row is null || row.Enabled || row.PendingSecretProtected is null || row.PendingCreatedAt is not { } created)
            return (false, "Start the set-up again.", null);
        if (Now - created >= PendingEnrollmentLifetime)
            return (false, "That set-up expired. Start again to get a new QR code.", null);
        if (IsLocked(row, out var until))
            return (false, LockoutMessage(until), null);

        var secret = Unprotect(row.PendingSecretProtected);
        var step = secret is null ? null : Totp.Verify(secret, code, Now);
        if (step is null)
        {
            var lockedNow = RegisterFailure(row);
            Audit(db, lockedNow ? "TwoFactor.Lockout" : "TwoFactor.EnrollmentFailed", user);
            await db.SaveChangesAsync();
            return (false, lockedNow ? LockoutMessage(row.LockoutUntil!.Value) : "That code did not match. Check the time on your phone is automatic and try the newest code.", null);
        }

        var codes = NewRecoveryCodes();
        row.Enabled = true;
        row.SecretProtected = row.PendingSecretProtected;
        row.PendingSecretProtected = null;
        row.PendingCreatedAt = null;
        row.EnabledAt = Now;
        row.LastUsedStep = step.Value;
        row.FailedAttempts = 0;
        row.LockoutUntil = null;
        row.RecoveryCodeHashes = HashCodes(codes);
        row.UpdatedAt = Now;
        Audit(db, "TwoFactor.Enabled", user);
        await db.SaveChangesAsync();
        return (true, null, codes);
    }

    /// <summary>The second sign-in step. Accepts a current authenticator code or an unused recovery code (which is then spent).</summary>
    public async Task<TwoFactorVerification> VerifyLoginAsync(int userId, string? input)
    {
        using var _ = await LockUserAsync(userId);
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        var row = await db.UserTwoFactors.FindAsync(userId);
        if (user is null || row is not { Enabled: true }) return new TwoFactorVerification(TwoFactorCheck.NotEnabled);

        var (result, justLocked) = VerifyCore(row, input);
        switch (result.Result)
        {
            case TwoFactorCheck.Success:
                Audit(db, "Login.TwoFactorSuccess", user, detail: result.UsedRecoveryCode ? "Recovery code" : "Authenticator code");
                if (result.UsedRecoveryCode)
                    Audit(db, "TwoFactor.RecoveryCodeUsed", user, detail: $"{result.RecoveryCodesLeft} left");
                break;
            case TwoFactorCheck.LockedOut:
                Audit(db, justLocked ? "Login.TwoFactorLockout" : "Login.TwoFactorBlockedByLockout", user);
                break;
            default:
                Audit(db, "Login.TwoFactorFailed", user);
                break;
        }
        await db.SaveChangesAsync();
        return result;
    }

    /// <summary>Turns 2FA off. Needs the account password AND a current code or recovery code, so a session left open is not enough.</summary>
    public async Task<(bool Ok, string? Error)> DisableAsync(int userId, string? currentPassword, string? code)
    {
        using var _ = await LockUserAsync(userId);
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        var row = await db.UserTwoFactors.FindAsync(userId);
        if (user is null || row is not { Enabled: true }) return (false, "Two-factor authentication is not on.");

        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(user.PasswordHash) || !PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            Audit(db, "TwoFactor.DisableFailed", user, detail: "Wrong password");
            await db.SaveChangesAsync();
            return (false, "Your current password is incorrect.");
        }

        var (result, _) = VerifyCore(row, code);
        if (result.Result != TwoFactorCheck.Success)
        {
            Audit(db, "TwoFactor.DisableFailed", user, detail: "Wrong code");
            await db.SaveChangesAsync();
            return (false, result.Result == TwoFactorCheck.LockedOut ? LockoutMessage(result.LockedUntil ?? Now + LockoutDuration) : "That code did not match.");
        }

        db.UserTwoFactors.Remove(row);
        Audit(db, "TwoFactor.Disabled", user);
        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Replaces every recovery code (old ones stop working). Needs a current authenticator code.</summary>
    public async Task<(bool Ok, string? Error, IReadOnlyList<string>? RecoveryCodes)> RegenerateRecoveryCodesAsync(int userId, string? code)
    {
        using var _ = await LockUserAsync(userId);
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        var row = await db.UserTwoFactors.FindAsync(userId);
        if (user is null || row is not { Enabled: true }) return (false, "Two-factor authentication is not on.", null);
        if (Totp.NormalizeCode(code) is null) return (false, "Enter the 6-digit code from your authenticator app.", null);

        var (result, _) = VerifyCore(row, code);
        if (result.Result != TwoFactorCheck.Success)
        {
            Audit(db, "TwoFactor.RecoveryCodesRegenerateFailed", user);
            await db.SaveChangesAsync();
            return (false, result.Result == TwoFactorCheck.LockedOut ? LockoutMessage(result.LockedUntil ?? Now + LockoutDuration) : "That code did not match.", null);
        }

        var codes = NewRecoveryCodes();
        row.RecoveryCodeHashes = HashCodes(codes);
        row.UpdatedAt = Now;
        Audit(db, "TwoFactor.RecoveryCodesRegenerated", user);
        await db.SaveChangesAsync();
        return (true, null, codes);
    }

    /// <summary>Admin escape hatch for someone who lost both their phone and their recovery codes. Also signs that account out everywhere.</summary>
    public async Task<(bool Ok, string? Error)> AdminResetAsync(int adminUserId, int targetUserId)
    {
        using var _ = await LockUserAsync(targetUserId);
        await using var db = await OpenAsync();
        var admin = await db.Users.FindAsync(adminUserId);
        if (admin is not { Role: UserRoles.Admin, Status: UserStatuses.Approved }) return (false, "Only an admin can reset two-factor authentication.");
        var target = await db.Users.FindAsync(targetUserId);
        if (target is null) return (false, "User not found.");

        var row = await db.UserTwoFactors.FindAsync(targetUserId);
        if (row is null) return (false, $"{target.Username} does not have two-factor authentication set up.");

        db.UserTwoFactors.Remove(row);
        // Whoever holds a session for that account proved a factor that is now gone - make them sign in again.
        target.SecurityStamp = Guid.NewGuid().ToString("N");
        Audit(db, "TwoFactor.ResetByAdmin", target, actor: admin);
        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>"Sign out everywhere": a fresh security stamp invalidates every cookie issued for this account, this one included
    /// (Program.cs's OnValidatePrincipal compares the stamp). Kept here with the rest of the self-service security actions.</summary>
    public async Task<bool> RevokeAllSessionsAsync(int userId)
    {
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        Audit(db, "Session.SignOutEverywhere", user);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<TwoFactorEnrolledUser>> ListEnrolledAsync()
    {
        await using var db = await OpenAsync();
        var rows = await db.UserTwoFactors.AsNoTracking().Where(t => t.Enabled).ToListAsync();
        var ids = rows.Select(r => r.UserId).ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);
        return rows.Where(r => names.ContainsKey(r.UserId))
            .Select(r => new TwoFactorEnrolledUser(r.UserId, names[r.UserId], r.EnabledAt, CountCodes(r.RecoveryCodeHashes)))
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- Core verification (caller holds the per-user lock and saves) ----

    /// <returns>The outcome, and whether this very attempt is the one that triggered a lockout.</returns>
    private (TwoFactorVerification Result, bool JustLocked) VerifyCore(UserTwoFactor row, string? input)
    {
        if (IsLocked(row, out var until)) return (new TwoFactorVerification(TwoFactorCheck.LockedOut, LockedUntil: until), false);

        if (Totp.NormalizeCode(input) is not null)
        {
            var secret = Unprotect(row.SecretProtected);
            var step = secret is null ? null : Totp.Verify(secret, input, Now, row.LastUsedStep);
            if (step is { } matched)
            {
                row.LastUsedStep = matched;
                ClearFailures(row);
                return (new TwoFactorVerification(TwoFactorCheck.Success, RecoveryCodesLeft: CountCodes(row.RecoveryCodeHashes)), false);
            }
        }
        else if (NormalizeRecoveryCode(input) is { } recovery && TryConsumeRecoveryCode(row, recovery))
        {
            ClearFailures(row);
            return (new TwoFactorVerification(TwoFactorCheck.Success, UsedRecoveryCode: true, RecoveryCodesLeft: CountCodes(row.RecoveryCodeHashes)), false);
        }

        return RegisterFailure(row)
            ? (new TwoFactorVerification(TwoFactorCheck.LockedOut, LockedUntil: row.LockoutUntil), true)
            : (new TwoFactorVerification(TwoFactorCheck.Invalid), false);
    }

    private bool IsLocked(UserTwoFactor row, out DateTimeOffset until)
    {
        until = row.LockoutUntil ?? default;
        return row.LockoutUntil is { } u && u > Now;
    }

    /// <returns>True when this failure triggered a lockout.</returns>
    private bool RegisterFailure(UserTwoFactor row)
    {
        row.FailedAttempts++;
        row.UpdatedAt = Now;
        if (row.FailedAttempts < MaxFailedAttempts) return false;
        row.FailedAttempts = 0;
        row.LockoutUntil = Now + LockoutDuration;
        _log.LogWarning("Two-factor code attempts locked for user {UserId} until {Until}", row.UserId, row.LockoutUntil);
        return true;
    }

    private void ClearFailures(UserTwoFactor row)
    {
        row.FailedAttempts = 0;
        row.LockoutUntil = null;
        row.UpdatedAt = Now;
    }

    private static string LockoutMessage(DateTimeOffset until) =>
        $"Too many wrong codes. Try again after {until.ToUniversalTime():HH:mm} UTC, or ask an admin.";

    private byte[]? Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try
        {
            return Base32.Decode(_protector.Unprotect(protectedSecret));
        }
        catch (CryptographicException ex)
        {
            // The Data Protection key ring changed (e.g. App_Data restored without dp-keys). Fail closed; an admin can reset.
            _log.LogError(ex, "A stored two-factor secret could not be decrypted - the Data Protection key ring may have changed.");
            return null;
        }
    }

    // ---- Recovery codes ----

    private static List<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var sb = new StringBuilder(RecoveryCodeLength + 1);
            for (var j = 0; j < RecoveryCodeLength; j++)
            {
                if (j == RecoveryCodeLength / 2) sb.Append('-');
                sb.Append(RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)]);
            }
            codes.Add(sb.ToString());
        }
        return codes;
    }

    /// <summary>Lower-cases and drops spaces/dashes; null unless it has the shape of a recovery code.</summary>
    public static string? NormalizeRecoveryCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var sb = new StringBuilder(RecoveryCodeLength);
        foreach (var c in input.Trim().ToLowerInvariant())
        {
            if (c is ' ' or '-') continue;
            if (!RecoveryAlphabet.Contains(c)) return null;
            sb.Append(c);
        }
        return sb.Length == RecoveryCodeLength ? sb.ToString() : null;
    }

    private static string HashCodes(IEnumerable<string> codes) =>
        string.Join('\n', codes.Select(c => HashRecoveryCode(NormalizeRecoveryCode(c)!)));

    internal static string HashRecoveryCode(string normalized)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(normalized, salt, RecoveryHashIterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    private static bool TryConsumeRecoveryCode(UserTwoFactor row, string normalized)
    {
        var lines = row.RecoveryCodeHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var matchIndex = -1;
        for (var i = 0; i < lines.Count; i++) // no early exit: same work whichever line matches
        {
            var parts = lines[i].Split('.');
            if (parts.Length != 2) continue;
            var actual = Rfc2898DeriveBytes.Pbkdf2(normalized, Convert.FromBase64String(parts[0]), RecoveryHashIterations, HashAlgorithmName.SHA256, 32);
            if (CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[1])) && matchIndex < 0) matchIndex = i;
        }
        if (matchIndex < 0) return false;
        lines.RemoveAt(matchIndex);
        row.RecoveryCodeHashes = string.Join('\n', lines);
        return true;
    }

    private static int CountCodes(string hashes) => hashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- Plumbing ----

    private async Task<IDisposable> LockUserAsync(int userId)
    {
        var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private void Audit(AiPulseDbContext db, string action, AppUser target, AppUser? actor = null, string? detail = null)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Action = action,
            TimestampUtc = Now,
            TargetUserId = target.Id,
            TargetUsername = target.Username,
            ActorUserId = actor?.Id ?? target.Id,
            ActorUsername = actor?.Username ?? target.Username,
            Detail = detail
        });
        _log.LogInformation("Security event {Action} for {Username}", action, target.Username);
    }

    /// <summary>Audit entry for events raised outside this service (challenge issued, sessions revoked).</summary>
    public async Task AuditAsync(int userId, string action, string? detail = null)
    {
        await using var db = await OpenAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return;
        Audit(db, action, user, detail: detail);
        await db.SaveChangesAsync();
    }

    private async Task<AiPulseDbContext> OpenAsync()
    {
        var db = await _dbFactory.CreateDbContextAsync();
        await EnsureSchemaAsync(db);
        return db;
    }

    /// <summary>Creates the UserTwoFactors table on databases that predate it (EnsureCreated only builds brand-new databases).</summary>
    private async Task EnsureSchemaAsync(AiPulseDbContext db)
    {
        if (_schemaEnsured) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaEnsured) return;
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "UserTwoFactors" (
                    "UserId" INTEGER NOT NULL CONSTRAINT "PK_UserTwoFactors" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL DEFAULT 0,
                    "SecretProtected" TEXT NOT NULL DEFAULT '',
                    "PendingSecretProtected" TEXT NULL,
                    "PendingCreatedAt" TEXT NULL,
                    "EnabledAt" TEXT NULL,
                    "LastUsedStep" INTEGER NOT NULL DEFAULT -1,
                    "FailedAttempts" INTEGER NOT NULL DEFAULT 0,
                    "LockoutUntil" TEXT NULL,
                    "RecoveryCodeHashes" TEXT NOT NULL DEFAULT '',
                    "UpdatedAt" TEXT NOT NULL DEFAULT ''
                )
                """);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
