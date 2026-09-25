using System.Security.Claims;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Infrastructure.Security;

/// <summary>
/// Builds a domain <see cref="Actor"/> from the authenticated principal.
/// </summary>
/// <remarks>
/// The single place where transport-level identity becomes domain identity. Keeping
/// it in one method means no controller can invent an actor from request data — the
/// role and user id always originate in the validated token.
/// </remarks>
public static class ClaimsPrincipalExtensions
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

    public static bool IsAdmin(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.IsInRole(AuthConstants.AdminRole);
    }
}
