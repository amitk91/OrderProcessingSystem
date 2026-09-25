using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OrderProcessing.Infrastructure.Security;

/// <summary>
/// Issues JWTs for the development token endpoint (specification section 8.1).
/// </summary>
/// <remarks>
/// Deliberately not a full identity system: there is no registration, password or
/// refresh-token flow, because the assignment's subject is order processing. The
/// endpoint that uses this is registered only in the Development environment.
/// </remarks>
public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    private readonly JwtOptions _options = options.Value;

    public string IssueToken(Guid userId, string role, string email)
    {
        var now = timeProvider.GetUtcNow();

        var claims = new List<Claim>
        {
            // The caller's identity comes from here and nowhere else — never from a
            // request body or query string (specification section 8.3).
            new(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("D")),
            new(ClaimTypes.Role, role)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(_options.TokenLifetime).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
