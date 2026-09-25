using System.Security.Claims;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Api.Security;

/// <summary>
/// Maps the authenticated principal onto a domain <see cref="Actor"/>.
/// </summary>
/// <remarks>
/// Lives in the API layer because <see cref="ClaimsPrincipal"/> is an ASP.NET concern:
/// the application layer takes an <see cref="Actor"/> and knows nothing about how the
/// caller was authenticated.
///
/// This is also the single place where transport identity becomes domain identity, so
/// no controller can invent an actor from request data — the role and user id always
/// originate in the validated token.
/// </remarks>
internal static class ClaimsPrincipalExtensions
{
    public static Actor ToActor(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var userId = principal.GetUserId();

        return principal.IsInRole(AuthConstants.AdminRole)
            ? Actor.Admin(userId)
            : Actor.Customer(userId);
    }

    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        return Guid.TryParse(subject, out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no usable subject claim.");
    }
}
