using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Application.Orders;

/// <summary>A requested line in a create-order command.</summary>
/// <remarks>
/// Carries no price: unit prices are resolved from the catalogue server-side, so a
/// client cannot influence what it is charged (FR-1.5, FR-1.6).
/// </remarks>
public sealed record CreateOrderItemCommand(Guid ProductId, int Quantity);

/// <summary>Request to place an order on behalf of the authenticated customer.</summary>
public sealed record CreateOrderCommand(
    Guid CustomerId,
    IReadOnlyList<CreateOrderItemCommand> Items,
    string? IdempotencyKey);

/// <summary>A single line of an order, as returned to clients.</summary>
public sealed record OrderItemDto(
    Guid ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

/// <summary>An entry in an order's audit trail.</summary>
public sealed record OrderStatusHistoryDto(
    string? FromStatus,
    string ToStatus,
    string ChangedBy,
    string? Reason,
    DateTimeOffset ChangedAt);

/// <summary>An order as returned to clients.</summary>
public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal TotalAmount,
    IReadOnlyList<OrderItemDto> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt,
    string? CancelledBy,
    string? CancellationReason,
    IReadOnlyList<OrderStatusHistoryDto> StatusHistory);

/// <summary>A page of results plus the metadata needed to navigate (FR-4.8).</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Sort fields permitted on the list endpoint (FR-4.7).</summary>
public enum OrderSortField
{
    CreatedAt,
    TotalAmount
}

/// <summary>Query parameters for listing orders.</summary>
/// <remarks>
/// <paramref name="CustomerId"/> is resolved from the caller's token for customers and
/// cannot be widened by a query parameter (FR-4.4); only admins may set it freely.
/// </remarks>
public sealed record ListOrdersQuery(
    Guid? CustomerId,
    OrderStatus? Status,
    int Page = 1,
    int PageSize = 20,
    OrderSortField SortBy = OrderSortField.CreatedAt,
    bool Descending = true);

/// <summary>
/// Result of a create-order request.
/// </summary>
/// <param name="WasCreated">
/// <see langword="false"/> when an idempotency key matched an existing order, so the
/// caller can answer 200 rather than 201 without having to infer it (FR-1.12).
/// </param>
public sealed record CreateOrderResult(OrderDto Order, bool WasCreated);
