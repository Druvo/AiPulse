using System.Text;
using AiPulse.Services.Security;
using Xunit;

namespace AiPulse.Tests;

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory] // RFC 4226 appendix D
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void Hotp_matches_the_rfc_4226_test_vectors(long counter, string expected) =>
        Assert.Equal(expected, Totp.Hotp(RfcSecret, counter));

    [Theory] // RFC 6238 appendix B, SHA1 rows (8 digits)
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_matches_the_rfc_6238_sha1_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.CodeAt(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8));

    [Fact]
    public void Six_digit_codes_are_the_low_six_digits_of_the_rfc_values()
    {
        Assert.Equal("287082", Totp.CodeAt(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal("081804", Totp.CodeAt(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(1111111109)));
    }

    [Fact]
    public void A_code_is_accepted_for_its_own_step_and_one_step_either_side_but_not_two()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step), now));
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step - 1), now));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step + 1), now));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step - 2), now));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step + 2), now));
    }

    [Fact]
    public void A_code_at_or_before_the_last_used_step_is_refused_so_it_cannot_be_replayed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Hotp(RfcSecret, step);
        Assert.Null(Totp.Verify(RfcSecret, code, now, lastUsedStep: step));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step - 1), now, lastUsedStep: step));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Hotp(RfcSecret, step + 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Typed_codes_are_normalised_or_rejected(string? input, string? expected) =>
        Assert.Equal(expected, Totp.NormalizeCode(input));

    [Fact]
    public void Wrong_and_malformed_codes_never_verify()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var right = Totp.CodeAt(RfcSecret, now);
        var wrong = ((int.Parse(right) + 1) % 1_000_000).ToString("000000");
        Assert.Null(Totp.Verify(RfcSecret, wrong, now));
        Assert.Null(Totp.Verify(RfcSecret, "abcdef", now));
        Assert.Null(Totp.Verify(RfcSecret, null, now));
    }

    [Theory] // RFC 4648 section 10
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc_4648_and_round_trips(string text, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(text, Encoding.ASCII.GetString(Base32.Decode(encoded)));
    }

    [Fact]
    public void Base32_decoding_tolerates_what_people_type_and_rejects_junk()
    {
        Assert.Equal("foobar", Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi======")));
        Assert.Throws<FormatException>(() => Base32.Decode("MZXW1"));
    }

    [Fact]
    public void Secrets_are_160_random_bits_and_the_manual_key_round_trips()
    {
        var a = Totp.NewSecret();
        var b = Totp.NewSecret();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, b);
        var manual = Totp.FormatForManualEntry(a);
        Assert.Matches("^([A-Z2-7]{4} ){7}[A-Z2-7]{4}$", manual);
        Assert.Equal(a, Base32.Decode(manual));
    }

    [Fact]
    public void The_otpauth_uri_follows_the_key_uri_format()
    {
        var uri = Totp.OtpAuthUri("AiPulse", "jo bloggs", RfcSecret);
        Assert.Equal("otpauth://totp/AiPulse:jo%20bloggs?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=AiPulse&algorithm=SHA1&digits=6&period=30", uri);
    }
}
