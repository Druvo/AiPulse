namespace AiPulse.Services;

/// <summary>
/// Bound from the "Jwt" section of appsettings.json. Backs the bearer tokens the mobile app uses, since
/// it can't carry AiPulse's cookie (<see cref="AuthOptions"/> / Program.cs's cookie scheme).
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Signing key (HMAC-SHA256). Change this in production - anyone with it can mint valid
    /// tokens for any user. At least 32 characters.</summary>
    public string Secret { get; set; } = "";

    public string Issuer { get; set; } = "AiPulse";

    public int ExpiryDays { get; set; } = 30;
}
