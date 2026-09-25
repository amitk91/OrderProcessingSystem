using Microsoft.EntityFrameworkCore;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Orders;

/// <summary>
/// Use cases for the order lifecycle (specification sections 7 and 11).
/// </summary>
/// <remarks>
/// Ownership scoping is applied here, in one place, via <see cref="ScopeToCaller"/>.
/// Every read path composes that method rather than loading an order and comparing
/// ids afterwards, so a new endpoint cannot accidentally omit the check
/// (specification section 8.3).
/// </remarks>
public sealed class OrderService(
    OrderProcessingDbContext dbContext,
    IOrderNumberGenerator orderNumberGenerator,
    TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public async Task<CreateOrderResult> CreateAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Items.Count == 0)
        {
            throw new EmptyOrderException();
        }

        // FR-1.12: an identical retry returns the original order rather than a duplicate.
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var existing = await LoadOrderGraph(dbContext.Orders.AsNoTracking())
                .FirstOrDefaultAsync(
                    order => order.CustomerId == command.CustomerId
                        && order.IdempotencyKey == command.IdempotencyKey,
                    cancellationToken);

            if (existing is not null)
            {
                EnsureIdempotentRequestMatches(existing, command);
                return new CreateOrderResult(ToDto(existing), WasCreated: false);
            }
        }

        var productIds = command.Items.Select(item => item.ProductId).Distinct().ToList();

        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

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

            // FR-1.5/1.6: the price comes from the catalogue, never from the request.
            lines.Add(new OrderLine(product.Id, product.Name, product.UnitPrice, item.Quantity));
        }

        var currency = products[productIds[0]].Currency;
        var now = timeProvider.GetUtcNow();

        var order = Order.Create(
            Guid.CreateVersion7(),
            await orderNumberGenerator.NextAsync(cancellationToken),
            command.CustomerId,
            lines,
            currency,
            now,
            string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey);

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateOrderResult(ToDto(order), WasCreated: true);
    }

    public async Task<OrderDto> GetAsync(
        Guid orderId,
        Actor caller,
        CancellationToken cancellationToken = default)
    {
        var order = await LoadOrderGraph(ScopeToCaller(dbContext.Orders.AsNoTracking(), caller))
            .FirstOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);

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

        var page = Math.Max(1, query.Page);

        // FR-4.6: an oversized page is clamped rather than rejected.
        var pageSize = query.PageSize <= 0
            ? DefaultPageSize
            : Math.Min(query.PageSize, MaxPageSize);

        var scoped = ScopeToCaller(dbContext.Orders.AsNoTracking(), caller);

        if (query.Status is { } status)
        {
            scoped = scoped.Where(order => order.Status == status);
        }

        // Admins may narrow to a specific customer; for customers this was already
        // fixed by ScopeToCaller and the parameter is ignored (FR-4.4, FR-4.5).
        if (caller.IsAdmin && query.CustomerId is { } customerId)
        {
            scoped = scoped.Where(order => order.CustomerId == customerId);
        }

        var totalCount = await scoped.CountAsync(cancellationToken);

        scoped = ApplySort(scoped, query.SortBy, query.Descending);

        var orders = await LoadOrderGraph(scoped)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderDto>(
            [.. orders.Select(ToDto)],
            page,
            pageSize,
            totalCount);
    }

    public async Task<OrderDto> TransitionAsync(
        Guid orderId,
        OrderStatus newStatus,
        Actor caller,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var order = await LoadOrderGraph(ScopeToCaller(dbContext.Orders, caller))
            .FirstOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken)
            ?? throw new OrderNotFoundException(orderId);

        if (order.TransitionTo(newStatus, caller, timeProvider.GetUtcNow(), reason))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
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
    /// Restricts a query to the orders the caller may see.
    /// </summary>
    /// <remarks>
    /// Ownership is pushed into the SQL predicate rather than checked after loading.
    /// Fetch-then-compare works until an endpoint forgets the comparison; scoping the
    /// query is fail-closed, because omitting it returns nothing rather than everything.
    /// </remarks>
    private static IQueryable<Order> ScopeToCaller(IQueryable<Order> source, Actor caller) =>
        caller.IsAdmin
            ? source
            : source.Where(order => order.CustomerId == caller.UserId);

    private static IQueryable<Order> LoadOrderGraph(IQueryable<Order> source) =>
        source
            .Include(order => order.Items)
            .Include(order => order.StatusHistory);

    private static IQueryable<Order> ApplySort(
        IQueryable<Order> source,
        OrderSortField sortBy,
        bool descending) =>
        (sortBy, descending) switch
        {
            // Sorting on the integer minor-unit column, which orders correctly.
            // A TEXT decimal column would sort "9.99" after "100.00" (section 5.4).
            (OrderSortField.TotalAmount, true) => source.OrderByDescending(o => o.TotalAmountMinor),
            (OrderSortField.TotalAmount, false) => source.OrderBy(o => o.TotalAmountMinor),
            (_, true) => source.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id),
            (_, false) => source.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
        };

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
