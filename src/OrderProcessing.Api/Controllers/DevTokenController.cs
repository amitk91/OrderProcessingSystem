using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderProcessing.Api.Contracts;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Infrastructure.Security;

namespace OrderProcessing.Api.Controllers;

/// <summary>Issued development token.</summary>
public sealed record DevTokenResponse(string Token, Guid UserId, string Role);

/// <summary>
/// Development-only token issuer (specification section 8.1).
/// </summary>
/// <remarks>
/// Registered only when the environment is Development — see the guard in
/// <c>Program.cs</c>. This exists so a reviewer can obtain a token and exercise the
/// API from Swagger without a full identity provider; it is emphatically not an
/// authentication system, and issues a token for any user id it is given.
/// </remarks>
[ApiController]
[Route("api/v1/dev")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class DevTokenController(JwtTokenIssuer tokenIssuer) : ControllerBase
{
    /// <summary>Issues a JWT for the given user and role.</summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(DevTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<DevTokenResponse> IssueToken([FromBody] DevTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = request.Role.Equals(AuthConstants.AdminRole, StringComparison.OrdinalIgnoreCase)
            ? AuthConstants.AdminRole
            : AuthConstants.CustomerRole;

        var token = tokenIssuer.IssueToken(request.UserId, role, request.Email);

        return Ok(new DevTokenResponse(token, request.UserId, role));
    }
}
