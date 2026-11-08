using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiPulse.Models;

/// <summary>
/// One account's two-factor (TOTP) state. Its own table rather than more columns on Users so the feature can be reset or
/// dropped without touching the account row, and so an account with no row simply has 2FA off. Secrets are stored encrypted
/// with Data Protection (never in plain text); recovery codes are stored only as salted PBKDF2 hashes.
/// </summary>
[Table("UserTwoFactors")]
public sealed class UserTwoFactor
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int UserId { get; set; }

    /// <summary>True once enrolment was confirmed with a valid code; only then does sign-in ask for one.</summary>
    public bool Enabled { get; set; }

    /// <summary>The confirmed secret, Data Protection-encrypted. Empty while 2FA is off.</summary>
    public string SecretProtected { get; set; } = "";

    /// <summary>A secret shown during enrolment but not yet confirmed (encrypted). Never used to sign in.</summary>
    public string? PendingSecretProtected { get; set; }
    public DateTimeOffset? PendingCreatedAt { get; set; }

    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>The last 30-second step whose code was accepted - codes for this step or older are refused (replay protection).</summary>
    public long LastUsedStep { get; set; } = -1;

    /// <summary>Wrong codes in a row; reaching the limit sets <see cref="LockoutUntil"/>. Separate from the password lockout.</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }

    /// <summary>Unused recovery codes, one "salt.hash" PBKDF2 entry per line. A used code's line is removed.</summary>
    public string RecoveryCodeHashes { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
