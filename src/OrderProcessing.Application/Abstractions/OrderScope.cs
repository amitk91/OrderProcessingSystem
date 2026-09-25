using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// The set of orders a caller is permitted to see, expressed as data rather than as a
/// query fragment (specification section 8.3).
/// </summary>
/// <remarks>
/// Modelling visibility as a value has two benefits over composing a LINQ predicate at
/// the call site:
/// <list type="number">
///   <item>The <em>decision</em> (who may see what) stays in the application layer,
///   while the <em>translation</em> to SQL stays in persistence. Neither leaks.</item>
///   <item>It can be made a required parameter on every read, so a caller cannot
///   forget to apply it — see <see cref="IOrderRepository"/>.</item>
/// </list>
/// </remarks>
public readonly record struct OrderScope
{
    private OrderScope(Guid? customerId) => CustomerId = customerId;

    /// <summary>
    /// The customer whose orders are visible, or <see langword="null"/> for unrestricted
    /// access.
    /// </summary>
    public Guid? CustomerId { get; }

    public bool IsUnrestricted => CustomerId is null;

    /// <summary>Every order. Only ever derived from an administrator principal.</summary>
    public static OrderScope All { get; } = new(customerId: null);

    public static OrderScope ForCustomer(Guid customerId)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id must not be empty.", nameof(customerId));
        }

        return new OrderScope(customerId);
    }

    /// <summary>
    /// Derives the visible set from the authenticated caller. This is the single place
    /// where "administrators see everything" is decided.
    /// </summary>
    public static OrderScope For(Actor caller) =>
        caller.IsAdmin
            ? All
            : ForCustomer(caller.UserId ?? throw new InvalidOperationException(
                "A non-administrator actor must carry a user id."));
}
