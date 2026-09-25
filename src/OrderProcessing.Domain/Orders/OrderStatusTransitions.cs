using System.Collections.Frozen;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// The authoritative transition rules for an order's lifecycle
/// (specification section 6.2).
/// </summary>
/// <remarks>
/// Transitions are governed by <em>both</em> the source state and the acting role,
/// which is why this is a <c>(actor, from) -&gt; allowed-to</c> map rather than a
/// simple <c>from -&gt; to</c> table. The practical consequence is that an admin may
/// cancel an order that is already <see cref="OrderStatus.Processing"/> while a
/// customer may not (specification section 6.3).
///
/// This type is the single point of enforcement. The API controllers and the
/// background promotion job both route through it, so there is no second copy of
/// these rules to drift out of step.
/// </remarks>
public static class OrderStatusTransitions
{
    private static readonly FrozenDictionary<(ActorType Actor, OrderStatus From), FrozenSet<OrderStatus>> Allowed =
        new Dictionary<(ActorType, OrderStatus), FrozenSet<OrderStatus>>
        {
            // A customer may only abandon an order that has not yet been worked on.
            [(ActorType.Customer, OrderStatus.Pending)] = Set(OrderStatus.Cancelled),
            [(ActorType.Customer, OrderStatus.Processing)] = Empty,
            [(ActorType.Customer, OrderStatus.Shipped)] = Empty,
            [(ActorType.Customer, OrderStatus.Delivered)] = Empty,
            [(ActorType.Customer, OrderStatus.Cancelled)] = Empty,

            // Operations drive fulfilment forward, and may cancel up to the point
            // of dispatch. Past Shipped, reversal is a returns flow (out of scope).
            [(ActorType.Admin, OrderStatus.Pending)] = Set(OrderStatus.Processing, OrderStatus.Cancelled),
            [(ActorType.Admin, OrderStatus.Processing)] = Set(OrderStatus.Shipped, OrderStatus.Cancelled),
            [(ActorType.Admin, OrderStatus.Shipped)] = Set(OrderStatus.Delivered),
            [(ActorType.Admin, OrderStatus.Delivered)] = Empty,
            [(ActorType.Admin, OrderStatus.Cancelled)] = Empty,

            // The background job has exactly one job: Pending -> Processing.
            // Deliberately no cancellation rights; nothing should cancel an order
            // without a human or a customer behind it.
            [(ActorType.System, OrderStatus.Pending)] = Set(OrderStatus.Processing),
            [(ActorType.System, OrderStatus.Processing)] = Empty,
            [(ActorType.System, OrderStatus.Shipped)] = Empty,
            [(ActorType.System, OrderStatus.Delivered)] = Empty,
            [(ActorType.System, OrderStatus.Cancelled)] = Empty
        }.ToFrozenDictionary();

    private static FrozenSet<OrderStatus> Empty => FrozenSet<OrderStatus>.Empty;

    private static FrozenSet<OrderStatus> Set(params OrderStatus[] statuses) =>
        statuses.ToFrozenSet();

    /// <summary>
    /// States that cannot be left, whoever is asking. Exposed so callers can
    /// distinguish "not allowed yet" from "never again".
    /// </summary>
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Delivered or OrderStatus.Cancelled;

    public static bool IsAllowed(ActorType actor, OrderStatus from, OrderStatus to) =>
        PermittedFrom(actor, from).Contains(to);

    /// <summary>
    /// The transitions <paramref name="actor"/> may make from <paramref name="from"/>.
    /// Returns an empty set for terminal states rather than throwing, so callers can
    /// present available actions without special-casing.
    /// </summary>
    public static IReadOnlySet<OrderStatus> PermittedFrom(ActorType actor, OrderStatus from) =>
        Allowed.TryGetValue((actor, from), out var permitted) ? permitted : Empty;

    /// <summary>
    /// Throws <see cref="InvalidStatusTransitionException"/> unless the transition is
    /// permitted. Used by the <see cref="Order"/> aggregate before any state is mutated,
    /// so a rejected transition leaves the order untouched.
    /// </summary>
    public static void EnsureAllowed(ActorType actor, OrderStatus from, OrderStatus to)
    {
        if (!IsAllowed(actor, from, to))
        {
            throw new InvalidStatusTransitionException(
                from,
                to,
                actor,
                [.. PermittedFrom(actor, from)]);
        }
    }
}
