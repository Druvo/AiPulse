using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AiPulse.Models;
using AiPulse.Models.Api;
using AiPulse.Services;
using AiPulse.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiPulse.Controllers.Api;

/// <summary>Mobile app login - trades a username/password (the same account the website uses) for a
/// bearer token, since the app can't carry AiPulse's cookie.</summary>
[ApiController]
[Route("api/mobile/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly UserService _users;
    private readonly JwtOptions _jwt;
    private readonly JwtSigningKey _signingKey;
    private readonly TwoFactorService _twoFactor;

    public AuthController(UserService users, IOptions<JwtOptions> jwt, JwtSigningKey signingKey, TwoFactorService twoFactor)
    {
        _users = users;
        _jwt = jwt.Value;
        _signingKey = signingKey;
        _twoFactor = twoFactor;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var (result, user) = await _users.TryLoginAsync(request.Username, request.Password);
        if (result != LoginResult.Success || user is null)
        {
            var message = result switch
            {
                LoginResult.PendingApproval => "Your account is pending admin approval.",
                LoginResult.Disabled => "This account has been disabled.",
                LoginResult.LockedOut => "Too many failed attempts. Try again later.",
                _ => "Invalid username or password."
            };
            return Unauthorized(new { error = message });
        }

        // Two-step sign-in applies to the app exactly as to the website: the password alone is not enough.
        if (await _twoFactor.IsEnabledAsync(user.Id))
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return Unauthorized(new { error = "Enter the code from your authenticator app.", twoFactorRequired = true });

            var check = await _twoFactor.VerifyLoginAsync(user.Id, request.Code);
            if (check.Result != TwoFactorCheck.Success)
            {
                var message = check.Result == TwoFactorCheck.LockedOut
                    ? "Too many wrong codes. Try again in a few minutes."
                    : "That code is not right. Try the next one.";
                return Unauthorized(new { error = message, twoFactorRequired = true });
            }
        }

        var expiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.ExpiryDays <= 0 ? 30 : _jwt.ExpiryDays);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("security_stamp", user.SecurityStamp)
        };
        var key = new SymmetricSecurityKey(_signingKey.Bytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Issuer,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return new LoginResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Username = user.Username,
            Role = user.Role,
            ExpiresAt = expiresAt
        };
    }
}
