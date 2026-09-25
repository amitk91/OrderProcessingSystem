using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// Raised when an order is created with no items (FR-1.2).
/// </summary>
public sealed class EmptyOrderException() : DomainException("An order must contain at least one item.")
{
    public override string ErrorCode => "empty-order";
}

/// <summary>
/// Raised when an administrator attempts to cancel without giving a reason (FR-5.4).
/// </summary>
public sealed class CancellationReasonRequiredException()
    : DomainException("A reason is required when an administrator cancels an order.")
{
    public override string ErrorCode => "cancellation-reason-required";
}
