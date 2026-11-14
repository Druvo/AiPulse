using System.Security.Cryptography;

namespace AiPulse.Services;

/// <summary>
/// The HMAC key that signs the mobile app's bearer tokens. Anyone holding it can mint a valid token for any account (admin included),
/// so it must never be a value that ships in the repository. Resolution order:
///  1. <c>Jwt:Secret</c> from configuration/environment, if an operator set a real one (at least 32 characters, not the old
///     placeholder text);
///  2. otherwise a random 48-byte key generated on first use and kept in <c>App_Data/jwt.key</c>, which survives deploys (the deploy
///     excludes App_Data) and is covered by backups.
/// Deleting the file signs every phone out (they simply log in again).
/// </summary>
public sealed class JwtSigningKey
{
    public const int MinLength = 32;
    private const string OldPlaceholderPrefix = "CHANGE-ME";

    public byte[] Bytes { get; }

    public JwtSigningKey(byte[] bytes)
    {
        if (bytes.Length < MinLength) throw new ArgumentException($"Signing key must be at least {MinLength} bytes.", nameof(bytes));
        Bytes = bytes;
    }

    public static JwtSigningKey Resolve(string? configuredSecret, string keyFilePath)
    {
        if (IsUsable(configuredSecret)) return new JwtSigningKey(System.Text.Encoding.UTF8.GetBytes(configuredSecret!));

        if (File.Exists(keyFilePath))
        {
            try
            {
                var existing = Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim());
                if (existing.Length >= MinLength) return new JwtSigningKey(existing);
            }
            catch (FormatException) { /* corrupt file: replace it below */ }
        }

        var key = RandomNumberGenerator.GetBytes(48);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        File.WriteAllText(keyFilePath, Convert.ToBase64String(key));
        return new JwtSigningKey(key);
    }

    public static bool IsUsable(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && secret.Length >= MinLength && !secret.StartsWith(OldPlaceholderPrefix, StringComparison.OrdinalIgnoreCase);
}
