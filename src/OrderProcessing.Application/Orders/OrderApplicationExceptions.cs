using OrderProcessing.Domain.Common;

namespace OrderProcessing.Application.Orders;

/// <summary>Raised when a referenced product does not exist (FR-1.3).</summary>
public sealed class ProductNotFoundException(Guid productId)
    : DomainException($"Product '{productId}' does not exist.")
{
    public Guid ProductId { get; } = productId;

    public override string ErrorCode => "product-not-found";
}

/// <summary>Raised when a referenced product exists but is not orderable (FR-1.3).</summary>
public sealed class ProductInactiveException(Guid productId, string productName)
    : DomainException($"Product '{productName}' is no longer available.")
{
    public Guid ProductId { get; } = productId;

    public override string ErrorCode => "product-inactive";
}

/// <summary>Raised when an order cannot be found, or is not visible to the caller.</summary>
/// <remarks>
/// Deliberately does not distinguish "does not exist" from "belongs to someone else":
/// both surface as 404 so an attacker cannot enumerate order ids
/// (specification section 8.4).
/// </remarks>
public sealed class OrderNotFoundException(Guid orderId)
    : DomainException($"Order '{orderId}' was not found.")
{
    public Guid OrderId { get; } = orderId;

    public override string ErrorCode => "order-not-found";
}

/// <summary>
/// Raised when an idempotency key is reused with a different payload (FR-1.12).
/// </summary>
public sealed class IdempotencyKeyConflictException(string key)
    : DomainException($"Idempotency key '{key}' was already used with a different request.")
{
    public override string ErrorCode => "idempotency-key-conflict";
}
