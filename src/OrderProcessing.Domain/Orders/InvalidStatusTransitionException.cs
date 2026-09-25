using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// Raised when a status change is rejected by the transition rules in
/// specification section 6.2. Maps to HTTP 409 Conflict: the request was
/// well-formed but conflicts with the order's current state.
/// </summary>
public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(
        OrderStatus from,
        OrderStatus to,
        ActorType actor,
        IReadOnlyCollection<OrderStatus> permitted)
        : base(BuildMessage(from, to, actor, permitted))
    {
        From = from;
        To = to;
        Actor = actor;
        Permitted = permitted;
    }

    public OrderStatus From { get; }

    public OrderStatus To { get; }

    public ActorType Actor { get; }

    /// <summary>
    /// The transitions this actor could legally have made instead. Returned to the
    /// client so an error is actionable rather than merely a rejection.
    /// </summary>
    public IReadOnlyCollection<OrderStatus> Permitted { get; }

    public override string ErrorCode => "invalid-status-transition";

    private static string BuildMessage(
        OrderStatus from,
        OrderStatus to,
        ActorType actor,
        IReadOnlyCollection<OrderStatus> permitted)
    {
        var allowed = permitted.Count == 0
            ? "no transitions are available from this state"
            : $"permitted transitions are: {string.Join(", ", permitted)}";

        return $"Cannot transition order from {from} to {to} as {actor}; {allowed}.";
    }
}
