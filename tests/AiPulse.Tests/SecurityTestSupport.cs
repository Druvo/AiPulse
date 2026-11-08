using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiPulse.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A throwaway SQLite database with the users schema, the bootstrapped admin, and a TwoFactorService on a manual clock.</summary>
internal sealed class SecurityRig : IDisposable
{
    public const string AdminPassword = "Adm1n-Throwaway!";
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "aipulse-sec-" + Guid.NewGuid().ToString("N"));
    public readonly IDbContextFactory<AiPulseDbContext> Db;
    public readonly UserService Users;
    public readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
    public readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 10, TimeSpan.Zero));
    public readonly TwoFactorService TwoFactor;
    public readonly RecordingEmail Email = new();

    public SecurityRig()
    {
        Directory.CreateDirectory(Dir);
        var options = new DbContextOptionsBuilder<AiPulseDbContext>().UseSqlite($"Data Source={Path.Combine(Dir, "t.db")}").Options;
        Db = new SimpleFactory(options);
        Users = new UserService(Db, Options.Create(new AuthOptions { Username = "admin", Password = AdminPassword }), Email, NullLogger<UserService>.Instance);
        Users.EnsureSchemaAndBootstrapAsync().GetAwaiter().GetResult();
        TwoFactor = new TwoFactorService(Db, Protection, NullLogger<TwoFactorService>.Instance, Clock);
    }

    public async Task<AppUser> AdminAsync() => (await Users.GetByUsernameAsync("admin"))!;

    public async Task<AppUser> UserAsync(string name = "alice", string password = "alice-pass-1")
    {
        await Users.CreateApprovedUserAsync(name, password, UserRoles.User);
        return (await Users.GetByUsernameAsync(name))!;
    }

    /// <summary>Enrols 2FA for the user and returns (secret, recovery codes). Leaves the clock one step later so codes are fresh.</summary>
    public async Task<(byte[] Secret, IReadOnlyList<string> Codes)> EnrolAsync(int userId)
    {
        var enrollment = await TwoFactor.BeginEnrollmentAsync(userId);
        var secret = Base32.Decode(enrollment.ManualKey);
        var (ok, error, codes) = await TwoFactor.ConfirmEnrollmentAsync(userId, Totp.CodeAt(secret, Clock.Now));
        if (!ok) throw new InvalidOperationException(error);
        Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return (secret, codes!);
    }

    public string CurrentCode(byte[] secret) => Totp.CodeAt(secret, Clock.Now);

    public async Task<List<string>> AuditActionsAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.AuditLog.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
    }

    public async Task<UserTwoFactor?> RowAsync(int userId)
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.UserTwoFactors.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { /* temp folder */ }
    }

    private sealed class SimpleFactory(DbContextOptions<AiPulseDbContext> options) : IDbContextFactory<AiPulseDbContext>
    {
        public AiPulseDbContext CreateDbContext() => new(options);
    }
}

internal sealed class RecordingEmail : IEmailSender
{
    public readonly List<(string To, string Body)> Sent = new();
    public bool Enabled => true;
    public Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add((toEmail, bodyText));
        return Task.CompletedTask;
    }
}
