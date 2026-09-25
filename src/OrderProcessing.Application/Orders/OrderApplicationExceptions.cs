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

/// <summary>
/// Raised when a concurrent transaction modified the same order first
/// (specification section 10.2).
/// </summary>
/// <remarks>
/// Translated from the persistence provider's own concurrency exception at the
/// repository boundary, so neither the application layer nor the API needs to know
/// which ORM is in use to handle a conflict.
/// </remarks>
public sealed class ConcurrencyConflictException(Guid orderId, Exception innerException)
    : DomainException(
        $"Order '{orderId}' was modified by another request. Reload it and retry.",
        innerException)
{
    public Guid OrderId { get; } = orderId;

    public override string ErrorCode => "concurrency-conflict";
}

/// <summary>
/// Signals that another request created an order with the same idempotency key while
/// this one was in flight (FR-1.12).
/// </summary>
/// <remarks>
/// <para>Raised by <see cref="Abstractions.IOrderRepository.SaveChangesAsync"/> when the
/// unique index on (customer, idempotency key) rejects an insert. It is an internal
/// signal, not an error: the application layer handles it by returning the order the
/// winning request created.</para>
///
/// <para>This exists because the idempotency check cannot be made race-free by reading
/// first. Two requests can both miss the read before either writes, so the database
/// constraint is the real enforcement and the violation is the signal that someone
/// else won.</para>
/// </remarks>
public sealed class DuplicateIdempotencyKeyException(string key, Exception innerException)
    : DomainException($"Idempotency key '{key}' was used by a concurrent request.", innerException)
{
    public string Key { get; } = key;

    public override string ErrorCode => "idempotency-key-race";
}

/// <summary>
/// Signals that two requests allocated the same order number concurrently (FR-1.10).
/// </summary>
/// <remarks>
/// Order numbers are allocated as "highest for the current year, plus one", so
/// concurrent callers can read the same maximum. Transient by nature: the application
/// layer retries with a freshly allocated number. A production deployment would use a
/// database sequence and avoid the collision entirely.
/// </remarks>
public sealed class DuplicateOrderNumberException(string orderNumber, Exception innerException)
    : DomainException($"Order number '{orderNumber}' was allocated concurrently.", innerException)
{
    public string OrderNumber { get; } = orderNumber;

    public override string ErrorCode => "order-number-race";
}
