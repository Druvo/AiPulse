using System.Security.Cryptography;
using System.Text;

namespace AiPulse.Services.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords (the codes Google Authenticator, 1Password, Authy, etc. show), built on RFC 4226
/// HOTP. Fixed to what every authenticator app supports: HMAC-SHA1, 6 digits, 30-second steps. Pure functions only - the
/// replay and throttling rules live in <see cref="TwoFactorService"/>, which owns the state they need.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    /// <summary>Steps either side of "now" that still count, to absorb clock drift between phone and server (RFC 6238 section 5.2).</summary>
    public const int AllowedDriftSteps = 1;
    /// <summary>160-bit secret: the HMAC-SHA1 block-size recommendation in RFC 4226 section 4 (R6).</summary>
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>RFC 4226 HOTP value for one counter, zero-padded to <paramref name="digits"/>.</summary>
    public static string Hotp(byte[] secret, long counter, int digits = Digits)
    {
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, hash);

        // Dynamic truncation (RFC 4226 section 5.3).
        var offset = hash[19] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = 1;
        for (var i = 0; i < digits; i++) modulus *= 10;
        return (binary % modulus).ToString(new string('0', digits));
    }

    public static string CodeAt(byte[] secret, DateTimeOffset time, int digits = Digits) => Hotp(secret, StepAt(time), digits);

    /// <summary>
    /// Checks <paramref name="code"/> against the steps around <paramref name="now"/> and returns the step it matched, or null.
    /// Steps at or before <paramref name="lastUsedStep"/> are never accepted, so a code that already signed someone in cannot be
    /// replayed - not even inside its own 30 seconds, and not an older code from the drift window either.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep = -1)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return null;

        var current = StepAt(now);
        long? matched = null;
        // Check every candidate (no early exit) so timing does not reveal which window matched.
        for (var step = current - AllowedDriftSteps; step <= current + AllowedDriftSteps; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Hotp(secret, step));
            var given = Encoding.ASCII.GetBytes(normalized);
            if (CryptographicOperations.FixedTimeEquals(expected, given) && step > lastUsedStep && matched is null)
                matched = step;
        }
        return matched;
    }

    /// <summary>Strips the spaces/dashes people type or paste ("123 456"); null unless exactly six digits remain.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var digits = new StringBuilder(Digits);
        foreach (var c in code)
        {
            if (c is ' ' or '-' or '\t') continue;
            if (c is < '0' or > '9') return null;
            digits.Append(c);
        }
        return digits.Length == Digits ? digits.ToString() : null;
    }

    /// <summary>
    /// The otpauth:// URI authenticator apps read from the QR code (Google Authenticator "Key Uri Format"). Issuer appears both
    /// as the label prefix and as a parameter, which is what current apps expect.
    /// </summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>The secret as people type it by hand: base32 in groups of four.</summary>
    public static string FormatForManualEntry(byte[] secret)
    {
        var raw = Base32.Encode(secret);
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }
}

/// <summary>RFC 4648 base32 (A-Z, 2-7), unpadded - the alphabet authenticator apps use for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Accepts lower case, spaces, dashes and '=' padding; throws FormatException on anything else.</summary>
    public static byte[] Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0) throw new FormatException($"'{raw}' is not a base32 character.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
