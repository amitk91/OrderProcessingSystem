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
/// An open unit-of-work transaction. Disposing without committing rolls back.
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}

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

    /// <summary>
    /// Loads the given orders for modification. Used by the promotion job after
    /// claiming, so the transition can be applied through the aggregate rather than in
    /// SQL.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no <see cref="OrderScope"/>: the caller is the background job
    /// acting as <c>System</c>, and the ids come from a claim it already holds a lock
    /// on. Ownership does not apply, which is why this is a separate method rather than
    /// an overload that would weaken the scoping guarantee elsewhere.
    /// </remarks>
    Task<IReadOnlyList<Order>> LoadForPromotionAsync(
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken cancellationToken = default);

    void Add(Order order);

    /// <summary>
    /// Begins a transaction spanning the claim and the promotion, so an order's status
    /// change and its audit entry commit together or not at all (FR-6.5).
    /// </summary>
    Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the unit of work. Throws <see cref="ConcurrencyConflictException"/> if
    /// another transaction modified the same order first.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
