using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Application.Orders;

/// <summary>
/// Order lifecycle use cases (specification section 7).
/// </summary>
/// <remarks>
/// This is the application layer: it orchestrates domain objects and persistence ports
/// but contains no persistence technology itself. It depends on
/// <see cref="IOrderRepository"/> and <see cref="IProductCatalog"/>, never on a
/// <c>DbContext</c>, so it can be exercised without a database and is unaffected by a
/// change of provider.
///
/// Two policies live here rather than in the domain or the controller, because they are
/// application concerns rather than invariants of an order:
/// <list type="bullet">
///   <item><b>Visibility</b> — translating the caller into an <see cref="OrderScope"/>
///   (specification section 8.3).</item>
///   <item><b>Query limits</b> — clamping page size, defaulting sort order
///   (FR-4.6, FR-4.7).</item>
/// </list>
/// </remarks>
public sealed class OrderService(
    IOrderRepository orders,
    IProductCatalog catalog,
    IOrderNumberGenerator orderNumberGenerator,
    TimeProvider timeProvider)
{
    internal const int MaxPageSize = 100;
    internal const int DefaultPageSize = 20;

    /// <summary>
    /// How many times creation is retried when a concurrent request wins a race for the
    /// same order number. Bounded so a pathological case fails rather than spins.
    /// </summary>
    private const int MaxCreateAttempts = 5;

    public async Task<CreateOrderResult> CreateAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Items.Count == 0)
        {
            throw new EmptyOrderException();
        }

        var hasIdempotencyKey = !string.IsNullOrWhiteSpace(command.IdempotencyKey);

        // FR-1.12: the fast path. A sequential retry finds the original here and never
        // reaches the insert. It cannot be relied on alone, though — two concurrent
        // requests can both miss this read before either writes, so the unique index is
        // the real enforcement and the loop below handles losing that race.
        if (hasIdempotencyKey)
        {
            var existing = await orders.FindByIdempotencyKeyAsync(
                command.CustomerId,
                command.IdempotencyKey!,
                cancellationToken);

            if (existing is not null)
            {
                EnsureIdempotentRequestMatches(existing, command);
                return new CreateOrderResult(ToDto(existing), WasCreated: false);
            }
        }

        var lines = await ResolveCatalogueLinesAsync(command, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var order = Order.Create(
                    Guid.CreateVersion7(),
                    await orderNumberGenerator.NextAsync(cancellationToken),
                    command.CustomerId,
                    lines,
                    timeProvider.GetUtcNow(),
                    hasIdempotencyKey ? command.IdempotencyKey : null);

                orders.Add(order);
                await orders.SaveChangesAsync(cancellationToken);

                return new CreateOrderResult(ToDto(order), WasCreated: true);
            }
            catch (DuplicateIdempotencyKeyException) when (hasIdempotencyKey)
            {
                // A concurrent request created the order first. That is the contract
                // working, not a failure: return what the winner created.
                var winner = await orders.FindByIdempotencyKeyAsync(
                    command.CustomerId,
                    command.IdempotencyKey!,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The idempotency key was rejected as duplicate but no matching order was found.");

                EnsureIdempotentRequestMatches(winner, command);
                return new CreateOrderResult(ToDto(winner), WasCreated: false);
            }
            catch (DuplicateOrderNumberException) when (attempt < MaxCreateAttempts)
            {
                // Two requests read the same "highest number" and allocated the same
                // value. Transient: the next attempt reads the number the winner
                // committed and moves past it.
            }
        }
    }

    public async Task<OrderDto> GetAsync(
        Guid orderId,
        Actor caller,
        CancellationToken cancellationToken = default)
    {
        var order = await orders.FindAsync(orderId, OrderScope.For(caller), cancellationToken);

        // FR-2.3: an order belonging to someone else is indistinguishable from one
        // that does not exist.
        return order is null ? throw new OrderNotFoundException(orderId) : ToDto(order);
    }

    public async Task<PagedResult<OrderDto>> ListAsync(
        ListOrdersQuery query,
        Actor caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var criteria = new OrderQueryCriteria(
            OrderScope.For(caller),
            query.Status,

            // FR-4.4/4.5: only an administrator may narrow to another customer. For a
            // customer the scope above already fixes visibility, so the parameter is
            // dropped rather than trusted.
            caller.IsAdmin ? query.CustomerId : null,
            Math.Max(1, query.Page),

            // FR-4.6: an oversized page is clamped rather than rejected.
            query.PageSize <= 0 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize),
            query.SortBy,
            query.Descending);

        var page = await orders.ListAsync(criteria, cancellationToken);

        return new PagedResult<OrderDto>(
            [.. page.Orders.Select(ToDto)],
            criteria.Page,
            criteria.PageSize,
            page.TotalCount);
    }

    public async Task<OrderDto> TransitionAsync(
        Guid orderId,
        OrderStatus newStatus,
        Actor caller,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var order = await orders.FindForUpdateAsync(orderId, OrderScope.For(caller), cancellationToken)
            ?? throw new OrderNotFoundException(orderId);

        // The aggregate decides whether the transition is legal; a same-status request
        // reports "nothing changed" so no write is issued (FR-3.5).
        if (order.TransitionTo(newStatus, caller, timeProvider.GetUtcNow(), reason))
        {
            await orders.SaveChangesAsync(cancellationToken);
        }

        return ToDto(order);
    }

    public Task<OrderDto> CancelAsync(
        Guid orderId,
        Actor caller,
        string? reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(orderId, OrderStatus.Cancelled, caller, reason, cancellationToken);

    /// <summary>
    /// Prices the requested lines against the catalogue (FR-1.3, FR-1.5, FR-1.6).
    /// </summary>
    private async Task<List<OrderLine>> ResolveCatalogueLinesAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var productIds = command.Items.Select(item => item.ProductId).Distinct().ToList();
        var products = await catalog.GetByIdsAsync(productIds, cancellationToken);

        var lines = new List<OrderLine>(command.Items.Count);

        foreach (var item in command.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                throw new ProductNotFoundException(item.ProductId);
            }

            if (!product.IsActive)
            {
                throw new ProductInactiveException(product.Id, product.Name);
            }

            // The price comes from the catalogue, never from the request, which is what
            // makes client-supplied prices irrelevant rather than merely ignored.
            lines.Add(new OrderLine(product.Id, product.Name, product.UnitPrice, item.Quantity));
        }

        return lines;
    }

    private static void EnsureIdempotentRequestMatches(Order existing, CreateOrderCommand command)
    {
        var requested = command.Items
            .GroupBy(item => item.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        var stored = existing.Items.ToDictionary(item => item.ProductId, item => item.Quantity);

        var matches = requested.Count == stored.Count
            && requested.All(pair =>
                stored.TryGetValue(pair.Key, out var quantity) && quantity == pair.Value);

        if (!matches)
        {
            throw new IdempotencyKeyConflictException(command.IdempotencyKey!);
        }
    }

    private static OrderDto ToDto(Order order) =>
        new(
            order.Id,
            order.OrderNumber,
            order.CustomerId,
            order.Status.ToString().ToUpperInvariant(),
            order.Currency,
            order.TotalAmount.Amount,
            [.. order.Items.Select(item => new OrderItemDto(
                item.ProductId,
                item.ProductName,
                item.UnitPrice.Amount,
                item.Quantity,
                item.LineTotal.Amount))],
            order.CreatedAt,
            order.UpdatedAt,
            order.CancelledAt,
            order.CancelledBy?.ToString().ToUpperInvariant(),
            order.CancellationReason,
            [.. order.StatusHistory
                .OrderBy(history => history.ChangedAt)
                .Select(history => new OrderStatusHistoryDto(
                    history.FromStatus?.ToString().ToUpperInvariant(),
                    history.ToStatus.ToString().ToUpperInvariant(),
                    history.ChangedBy.ToString().ToUpperInvariant(),
                    history.Reason,
                    history.ChangedAt))]);
}
