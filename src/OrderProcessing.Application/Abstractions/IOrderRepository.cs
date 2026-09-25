using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Application.Abstractions;

/// <summary>Criteria for a paged order query, already validated and clamped.</summary>
public sealed record OrderQueryCriteria(
    OrderScope Scope,
    OrderStatus? Status,
    Guid? CustomerId,
    int Page,
    int PageSize,
    OrderSortField SortBy,
    bool Descending);

/// <summary>A page of orders together with the total matching count.</summary>
public sealed record OrderPage(IReadOnlyList<Order> Orders, int TotalCount);

/// <summary>
/// Persistence port for the order aggregate (specification section 4).
/// </summary>
/// <remarks>
/// Every read takes an <see cref="OrderScope"/> as a <em>required</em> parameter. That
/// is the mechanism that makes ownership enforcement fail-closed: there is no overload
/// that omits it, so a new call site cannot silently query across customers. The
/// compiler, not code review, is what enforces it.
///
/// The interface deals in domain aggregates and plain criteria objects. No
/// <c>IQueryable</c> crosses this boundary, so the application layer needs no knowledge
/// of the persistence technology and can be unit-tested against a substitute.
/// </remarks>
public interface IOrderRepository
{
    /// <summary>
    /// Loads an order for reading, or <see langword="null"/> if it does not exist or
    /// falls outside <paramref name="scope"/> — the two cases are deliberately
    /// indistinguishable (specification section 8.4).
    /// </summary>
    Task<Order?> FindAsync(Guid orderId, OrderScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads an order for modification, tracked by the unit of work so that
    /// <see cref="SaveChangesAsync"/> persists any transition applied to it.
    /// </summary>
    Task<Order?> FindForUpdateAsync(Guid orderId, OrderScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a previous order created with the same idempotency key for this customer
    /// (FR-1.12). Keys are scoped per customer, so one customer's key cannot collide
    /// with another's.
    /// </summary>
    Task<Order?> FindByIdempotencyKeyAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<OrderPage> ListAsync(OrderQueryCriteria criteria, CancellationToken cancellationToken = default);

    void Add(Order order);

    /// <summary>
    /// Appends audit entries for transitions applied outside the aggregate — currently
    /// only the background promotion, which is performed by a single atomic statement
    /// (specification section 9.3).
    /// </summary>
    void AddStatusHistory(IEnumerable<OrderStatusHistory> entries);

    /// <summary>
    /// Commits the unit of work. Throws <see cref="ConcurrencyConflictException"/> if
    /// another transaction modified the same order first.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
