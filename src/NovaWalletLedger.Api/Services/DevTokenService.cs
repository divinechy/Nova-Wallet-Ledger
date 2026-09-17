using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NovaWalletLedger.Api.Services;

/// <summary>
/// Mints JWTs for local testing/demoing. This intentionally stands in for a real identity
/// provider (Keycloak, Azure AD B2C, an internal auth service, etc.) — the point of this
/// take-home is the resource-server side (bearer validation + claims handling on the
/// wallet endpoints), not building an issuer. Never do this in a real system: a real
/// issuer authenticates the caller before handing out a token.
/// </summary>
public class DevTokenService
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SymmetricSecurityKey _key;

    public DevTokenService(IConfiguration config)
    {
        _issuer = config["Jwt:Issuer"]!;
        _audience = config["Jwt:Audience"]!;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!));
    }

    public (string token, DateTimeOffset expiresAtUtc) IssueToken(string customerId)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, customerId),
            new Claim("cid", customerId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
