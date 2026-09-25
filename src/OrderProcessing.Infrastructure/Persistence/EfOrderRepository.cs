using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/>.
/// </summary>
/// <remarks>
/// All LINQ composition stops here. The application layer passes an
/// <see cref="OrderScope"/> and plain criteria; translating those into a query — and
/// translating a provider's concurrency exception back out — is this class's entire job.
/// </remarks>
internal sealed class EfOrderRepository(OrderProcessingDbContext dbContext) : IOrderRepository
{
    /// <summary>
    /// Shadow property holding the promotion lease. Defined on the persistence model
    /// only, so the scheduling mechanism never appears on the domain aggregate.
    /// </summary>
    internal const string PromotionLeaseProperty = "PromotionLease";

    public async Task<Order?> FindAsync(
        Guid orderId,
        OrderScope scope,
        CancellationToken cancellationToken = default) =>
        await WithGraph(Scoped(dbContext.Orders.AsNoTracking(), scope))
            .FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);

    public async Task<Order?> FindForUpdateAsync(
        Guid orderId,
        OrderScope scope,
        CancellationToken cancellationToken = default) =>
        await WithGraph(Scoped(dbContext.Orders, scope))
            .FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);

    public async Task<Order?> FindByIdempotencyKeyAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await WithGraph(dbContext.Orders.AsNoTracking())
            .FirstOrDefaultAsync(
                order => order.CustomerId == customerId && order.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public async Task<OrderPage> ListAsync(
        OrderQueryCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var query = Scoped(dbContext.Orders.AsNoTracking(), criteria.Scope);

        if (criteria.Status is { } status)
        {
            query = query.Where(order => order.Status == status);
        }

        // Already resolved by the application layer: null unless the caller is an
        // administrator who asked to narrow to one customer.
        if (criteria.CustomerId is { } customerId)
        {
            query = query.Where(order => order.CustomerId == customerId);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var orders = await WithGraph(ApplySort(query, criteria.SortBy, criteria.Descending))
            .Skip((criteria.Page - 1) * criteria.PageSize)
            .Take(criteria.PageSize)
            .ToListAsync(cancellationToken);

        return new OrderPage(orders, totalCount);
    }

    public async Task<IReadOnlyList<Order>> LoadForPromotionAsync(
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        if (orderIds.Count == 0)
        {
            return [];
        }

        var orders = await WithGraph(dbContext.Orders)
            .Where(order => orderIds.Contains(order.Id))
            .ToListAsync(cancellationToken);

        // Release the lease as part of the same unit of work. The claim exists only to
        // stop two workers picking up the same order; once the aggregate has been
        // loaded for promotion it has served its purpose, and clearing it here means a
        // lease can never outlive its transaction.
        foreach (var order in orders)
        {
            dbContext.Entry(order).Property(PromotionLeaseProperty).CurrentValue = null;
        }

        return orders;
    }

    public void Add(Order order) => dbContext.Orders.Add(order);

    public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new EfTransaction(transaction);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Translated at the boundary so neither the application layer nor the API
            // needs to know which ORM raised it.
            var orderId = ex.Entries.Count > 0 && ex.Entries[0].Entity is Order order
                ? order.Id
                : Guid.Empty;

            throw new ConcurrencyConflictException(orderId, ex);
        }
    }

    /// <summary>Adapts EF Core's transaction to the application-layer port.</summary>
    private sealed class EfTransaction(IDbContextTransaction transaction) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    /// <summary>
    /// Applies the caller's visibility. Taking <see cref="OrderScope"/> by value rather
    /// than composing a predicate at the call site is what lets the port make it a
    /// required argument on every read.
    /// </summary>
    private static IQueryable<Order> Scoped(IQueryable<Order> source, OrderScope scope) =>
        scope.IsUnrestricted
            ? source
            : source.Where(order => order.CustomerId == scope.CustomerId);

    private static IQueryable<Order> WithGraph(IQueryable<Order> source) =>
        source
            .Include(order => order.Items)
            .Include(order => order.StatusHistory);

    private static IQueryable<Order> ApplySort(
        IQueryable<Order> source,
        OrderSortField sortBy,
        bool descending) =>
        (sortBy, descending) switch
        {
            // Sorting on the integer minor-unit column, which orders correctly. A TEXT
            // decimal column would sort "9.99" after "100.00" (section 5.4).
            (OrderSortField.TotalAmount, true) => source.OrderByDescending(o => o.TotalAmountMinor),
            (OrderSortField.TotalAmount, false) => source.OrderBy(o => o.TotalAmountMinor),

            // Id is a tie-breaker so paging cannot repeat or skip rows that share a
            // timestamp.
            (_, true) => source.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id),
            (_, false) => source.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
        };
}
