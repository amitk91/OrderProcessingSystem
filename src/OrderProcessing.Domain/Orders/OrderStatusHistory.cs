namespace OrderProcessing.Domain.Orders;

/// <summary>
/// An immutable audit record of a single status change
/// (specification section 5.1, FR-3.4, FR-5.6).
/// </summary>
/// <remarks>
/// Recorded for every transition including the initial creation, where
/// <see cref="FromStatus"/> is null. This trail is what makes an administrative
/// cancellation defensible after the fact.
/// </remarks>
public sealed class OrderStatusHistory
{
    private OrderStatusHistory()
    {
        // EF Core materialisation.
    }

    private OrderStatusHistory(
        Guid id,
        Guid orderId,
        OrderStatus? fromStatus,
        OrderStatus toStatus,
        ActorType changedBy,
        Guid? changedByUserId,
        string? reason,
        DateTimeOffset changedAt)
    {
        Id = id;
        OrderId = orderId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ChangedBy = changedBy;
        ChangedByUserId = changedByUserId;
        Reason = reason;
        ChangedAt = changedAt;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>Null for the record written when the order is created.</summary>
    public OrderStatus? FromStatus { get; private set; }

    public OrderStatus ToStatus { get; private set; }

    public ActorType ChangedBy { get; private set; }

    /// <summary>Null for transitions made by the background job.</summary>
    public Guid? ChangedByUserId { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    internal static OrderStatusHistory Record(
        Guid orderId,
        OrderStatus? fromStatus,
        OrderStatus toStatus,
        Actor actor,
        string? reason,
        DateTimeOffset changedAt) =>
        new(
            Guid.CreateVersion7(),
            orderId,
            fromStatus,
            toStatus,
            actor.Type,
            actor.UserId,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            changedAt);
}
