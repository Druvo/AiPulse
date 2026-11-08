using AiPulse.Models;
using AiPulse.Services;
using AiPulse.Services.Security;
using Xunit;

namespace AiPulse.Tests;

public class TwoFactorServiceTests
{
    [Fact]
    public async Task Enrolment_turns_on_only_after_a_valid_code_and_hands_out_ten_recovery_codes()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();

        var enrollment = await rig.TwoFactor.BeginEnrollmentAsync(alice.Id);
        Assert.StartsWith("otpauth://totp/AiPulse:alice?secret=", enrollment.OtpAuthUri);
        Assert.StartsWith("<svg", enrollment.QrSvg);
        Assert.False(await rig.TwoFactor.IsEnabledAsync(alice.Id));
        Assert.True((await rig.TwoFactor.GetStatusAsync(alice.Id)).EnrollmentPending);

        var secret = Base32.Decode(enrollment.ManualKey);
        var (ok, _, codes) = await rig.TwoFactor.ConfirmEnrollmentAsync(alice.Id, rig.CurrentCode(secret));

        Assert.True(ok);
        Assert.True(await rig.TwoFactor.IsEnabledAsync(alice.Id));
        Assert.Equal(10, codes!.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[2-9a-z]{5}-[2-9a-z]{5}$", c));
        Assert.Equal(10, (await rig.TwoFactor.GetStatusAsync(alice.Id)).RecoveryCodesLeft);
        Assert.Contains("TwoFactor.EnrollmentStarted", await rig.AuditActionsAsync());
        Assert.Contains("TwoFactor.Enabled", await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task A_wrong_enrolment_code_leaves_it_off_and_is_audited()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        await rig.TwoFactor.BeginEnrollmentAsync(alice.Id);

        var (ok, error, codes) = await rig.TwoFactor.ConfirmEnrollmentAsync(alice.Id, "000000");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Null(codes);
        Assert.False(await rig.TwoFactor.IsEnabledAsync(alice.Id));
        Assert.Contains("TwoFactor.EnrollmentFailed", await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task A_pending_enrolment_expires()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var secret = Base32.Decode((await rig.TwoFactor.BeginEnrollmentAsync(alice.Id)).ManualKey);
        rig.Clock.Advance(TwoFactorService.PendingEnrollmentLifetime + TimeSpan.FromMinutes(1));

        var (ok, error, _) = await rig.TwoFactor.ConfirmEnrollmentAsync(alice.Id, rig.CurrentCode(secret));

        Assert.False(ok);
        Assert.Contains("expired", error);
    }

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_recovery_codes_are_only_stored_hashed()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, codes) = await rig.EnrolAsync(alice.Id);

        var row = (await rig.RowAsync(alice.Id))!;
        var base32 = Base32.Encode(secret);
        Assert.DoesNotContain(base32, row.SecretProtected);
        Assert.NotEqual("", row.SecretProtected);
        foreach (var code in codes)
        {
            Assert.DoesNotContain(code, row.RecoveryCodeHashes);
            Assert.DoesNotContain(code.Replace("-", ""), row.RecoveryCodeHashes);
        }
        Assert.Equal(10, row.RecoveryCodeHashes.Split('\n').Length);
    }

    [Fact]
    public async Task External_only_accounts_cannot_enrol()
    {
        using var rig = new SecurityRig();
        await using (var db = await rig.Db.CreateDbContextAsync())
        {
            db.Users.Add(new AppUser { Username = "oauthie", PasswordHash = "", Status = UserStatuses.Approved });
            await db.SaveChangesAsync();
        }
        var user = await rig.Users.GetByUsernameAsync("oauthie");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.TwoFactor.BeginEnrollmentAsync(user!.Id));
    }

    [Fact]
    public async Task Sign_in_accepts_the_current_code_once_and_refuses_a_replay()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, _) = await rig.EnrolAsync(alice.Id);
        var code = rig.CurrentCode(secret);

        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, code)).Result);
        Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, code)).Result);

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, rig.CurrentCode(secret))).Result);

        var audit = await rig.AuditActionsAsync();
        Assert.Equal(2, audit.Count(a => a == "Login.TwoFactorSuccess"));
        Assert.Contains("Login.TwoFactorFailed", audit);
    }

    [Fact]
    public async Task The_enrolment_code_itself_cannot_be_reused_to_sign_in()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var secret = Base32.Decode((await rig.TwoFactor.BeginEnrollmentAsync(alice.Id)).ManualKey);
        var code = rig.CurrentCode(secret);
        Assert.True((await rig.TwoFactor.ConfirmEnrollmentAsync(alice.Id, code)).Ok);

        Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, code)).Result);
    }

    [Fact]
    public async Task Codes_from_one_step_either_side_are_accepted_for_clock_drift()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, _) = await rig.EnrolAsync(alice.Id);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var oneStepAhead = Totp.CodeAt(secret, rig.Clock.Now.AddSeconds(30));
        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, oneStepAhead)).Result);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        var twoStepsBehind = Totp.CodeAt(secret, rig.Clock.Now.AddSeconds(-60));
        Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, twoStepsBehind)).Result);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_for_fifteen_minutes_even_against_the_right_code()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, _) = await rig.EnrolAsync(alice.Id);

        for (var i = 0; i < TwoFactorService.MaxFailedAttempts - 1; i++)
            Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, "000000")).Result);
        var fifth = await rig.TwoFactor.VerifyLoginAsync(alice.Id, "000000");
        Assert.Equal(TwoFactorCheck.LockedOut, fifth.Result);
        Assert.Equal(rig.Clock.Now + TwoFactorService.LockoutDuration, fifth.LockedUntil);

        Assert.Equal(TwoFactorCheck.LockedOut, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, rig.CurrentCode(secret))).Result);

        rig.Clock.Advance(TwoFactorService.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, rig.CurrentCode(secret))).Result);

        var audit = await rig.AuditActionsAsync();
        Assert.Contains("Login.TwoFactorLockout", audit);
        Assert.Contains("Login.TwoFactorBlockedByLockout", audit);
    }

    [Fact]
    public async Task A_success_resets_the_wrong_code_counter()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, _) = await rig.EnrolAsync(alice.Id);

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < TwoFactorService.MaxFailedAttempts - 1; i++)
                await rig.TwoFactor.VerifyLoginAsync(alice.Id, "000000");
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, rig.CurrentCode(secret))).Result);
        }
    }

    [Fact]
    public async Task Lockouts_are_per_account()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var bob = await rig.UserAsync("bobby", "bob-pass-1");
        await rig.EnrolAsync(alice.Id);
        var (bobSecret, _) = await rig.EnrolAsync(bob.Id);

        for (var i = 0; i < TwoFactorService.MaxFailedAttempts; i++)
            await rig.TwoFactor.VerifyLoginAsync(alice.Id, "000000");

        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(bob.Id, rig.CurrentCode(bobSecret))).Result);
    }

    [Fact]
    public async Task Each_recovery_code_works_exactly_once_in_any_typing()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (_, codes) = await rig.EnrolAsync(alice.Id);

        var first = await rig.TwoFactor.VerifyLoginAsync(alice.Id, " " + codes[3].ToUpperInvariant().Replace("-", " ") + " ");
        Assert.Equal(TwoFactorCheck.Success, first.Result);
        Assert.True(first.UsedRecoveryCode);
        Assert.Equal(9, first.RecoveryCodesLeft);

        Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, codes[3])).Result);
        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, codes[4])).Result);
        Assert.Equal(8, (await rig.TwoFactor.GetStatusAsync(alice.Id)).RecoveryCodesLeft);
        Assert.Contains("TwoFactor.RecoveryCodeUsed", await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task Wrong_recovery_codes_count_towards_the_lockout()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        await rig.EnrolAsync(alice.Id);
        TwoFactorVerification last = null!;
        for (var i = 0; i < TwoFactorService.MaxFailedAttempts; i++)
            last = await rig.TwoFactor.VerifyLoginAsync(alice.Id, "aaaaa-bbbbb");
        Assert.Equal(TwoFactorCheck.LockedOut, last.Result);
    }

    [Fact]
    public async Task Regenerating_recovery_codes_needs_a_code_and_retires_the_old_ones()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, oldCodes) = await rig.EnrolAsync(alice.Id);

        Assert.False((await rig.TwoFactor.RegenerateRecoveryCodesAsync(alice.Id, "000000")).Ok);
        Assert.False((await rig.TwoFactor.RegenerateRecoveryCodesAsync(alice.Id, oldCodes[0])).Ok); // a recovery code is not enough

        var (ok, _, newCodes) = await rig.TwoFactor.RegenerateRecoveryCodesAsync(alice.Id, rig.CurrentCode(secret));
        Assert.True(ok);
        Assert.Empty(newCodes!.Intersect(oldCodes));
        Assert.Equal(TwoFactorCheck.Invalid, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, oldCodes[1])).Result);
        Assert.Equal(TwoFactorCheck.Success, (await rig.TwoFactor.VerifyLoginAsync(alice.Id, newCodes![0])).Result);
        Assert.Contains("TwoFactor.RecoveryCodesRegenerated", await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task Turning_it_off_needs_both_the_password_and_a_code()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, codes) = await rig.EnrolAsync(alice.Id);

        Assert.False((await rig.TwoFactor.DisableAsync(alice.Id, "wrong-password", rig.CurrentCode(secret))).Ok);
        Assert.False((await rig.TwoFactor.DisableAsync(alice.Id, "alice-pass-1", "000000")).Ok);
        Assert.False((await rig.TwoFactor.DisableAsync(alice.Id, "alice-pass-1", null)).Ok);
        Assert.True(await rig.TwoFactor.IsEnabledAsync(alice.Id));

        Assert.True((await rig.TwoFactor.DisableAsync(alice.Id, "alice-pass-1", codes[0])).Ok);
        Assert.False(await rig.TwoFactor.IsEnabledAsync(alice.Id));
        Assert.Null(await rig.RowAsync(alice.Id));

        var audit = await rig.AuditActionsAsync();
        Assert.Equal(3, audit.Count(a => a == "TwoFactor.DisableFailed"));
        Assert.Contains("TwoFactor.Disabled", audit);
    }

    [Fact]
    public async Task An_admin_can_reset_someones_2fa_which_signs_them_out_and_is_audited_with_the_admin_as_actor()
    {
        using var rig = new SecurityRig();
        var admin = await rig.AdminAsync();
        var alice = await rig.UserAsync();
        await rig.EnrolAsync(alice.Id);
        var stampBefore = alice.SecurityStamp;

        var (ok, _) = await rig.TwoFactor.AdminResetAsync(admin.Id, alice.Id);

        Assert.True(ok);
        Assert.False(await rig.TwoFactor.IsEnabledAsync(alice.Id));
        Assert.NotEqual(stampBefore, (await rig.Users.GetByIdAsync(alice.Id))!.SecurityStamp);
        await using var db = await rig.Db.CreateDbContextAsync();
        var entry = db.AuditLog.Single(a => a.Action == "TwoFactor.ResetByAdmin");
        Assert.Equal(admin.Id, entry.ActorUserId);
        Assert.Equal(alice.Id, entry.TargetUserId);
    }

    [Fact]
    public async Task A_non_admin_cannot_reset_anyones_2fa()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var bob = await rig.UserAsync("bobby", "bob-pass-1");
        await rig.EnrolAsync(alice.Id);

        var (ok, _) = await rig.TwoFactor.AdminResetAsync(bob.Id, alice.Id);

        Assert.False(ok);
        Assert.True(await rig.TwoFactor.IsEnabledAsync(alice.Id));
    }

    [Fact]
    public async Task The_admin_list_shows_only_enrolled_accounts()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        await rig.UserAsync("bobby", "bob-pass-1");
        await rig.EnrolAsync(alice.Id);

        var list = await rig.TwoFactor.ListEnrolledAsync();

        Assert.Equal("alice", Assert.Single(list).Username);
    }

    [Fact]
    public async Task Sign_out_everywhere_rotates_the_security_stamp_and_is_audited()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();

        Assert.True(await rig.TwoFactor.RevokeAllSessionsAsync(alice.Id));

        Assert.NotEqual(alice.SecurityStamp, (await rig.Users.GetByIdAsync(alice.Id))!.SecurityStamp);
        Assert.Contains("Session.SignOutEverywhere", await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task A_secret_written_under_another_key_ring_fails_closed()
    {
        using var rig = new SecurityRig();
        var alice = await rig.UserAsync();
        var (secret, _) = await rig.EnrolAsync(alice.Id);

        var otherKeys = new TwoFactorService(rig.Db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TwoFactorService>.Instance, rig.Clock);

        Assert.Equal(TwoFactorCheck.Invalid, (await otherKeys.VerifyLoginAsync(alice.Id, rig.CurrentCode(secret))).Result);
    }

    [Theory]
    [InlineData("abcde-23456", "abcde23456")]
    [InlineData(" ABCDE 23456 ", "abcde23456")]
    [InlineData("abcde-2345", null)]
    [InlineData("abcd0-23456", null)] // 0 is not in the alphabet
    [InlineData("123456", null)]
    public void Recovery_codes_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, TwoFactorService.NormalizeRecoveryCode(input));
}
