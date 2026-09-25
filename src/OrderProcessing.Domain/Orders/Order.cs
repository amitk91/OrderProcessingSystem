using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// The order aggregate root (specification section 5.1).
/// </summary>
/// <remarks>
/// All state changes go through this type. In particular every status change routes
/// through <see cref="TransitionTo"/>, which is the single point where the transition
/// matrix in section 6.2 is enforced — the API controllers and the background job both
/// call it, so there is no second copy of those rules to drift.
///
/// Totals are computed here from the line items and are never accepted from a caller
/// (FR-1.7). Item prices are snapshots supplied by the application layer after being
/// resolved from the catalogue (FR-1.5).
/// </remarks>
public sealed class Order
{
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderStatusHistory> _statusHistory = [];

    private Order()
    {
        // EF Core materialisation.
        OrderNumber = null!;
        Currency = null!;
    }

    private Order(
        Guid id,
        string orderNumber,
        Guid customerId,
        string currency,
        string? idempotencyKey,
        DateTimeOffset createdAt)
    {
        Id = id;
        OrderNumber = orderNumber;
        CustomerId = customerId;
        Currency = currency;
        IdempotencyKey = idempotencyKey;
        Status = OrderStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>Human-readable identifier, for example <c>ORD-2026-000042</c> (FR-1.10).</summary>
    public string OrderNumber { get; private set; }

    public Guid CustomerId { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>Order total in minor units. See <see cref="TotalAmount"/> for the typed view.</summary>
    public long TotalAmountMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Client-supplied key making order creation idempotent (FR-1.12).</summary>
    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public ActorType? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>
    /// Optimistic concurrency token, incremented on every mutation
    /// (specification section 10.2).
    /// </summary>
    /// <remarks>
    /// Managed in application code rather than by the database because SQLite has no
    /// server-generated row version. EF Core is configured to treat this as a
    /// concurrency token, so a concurrent write is detected rather than silently lost —
    /// which is what makes the cancel-versus-scheduler race deterministic.
    /// </remarks>
    public int Version { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public IReadOnlyList<OrderStatusHistory> StatusHistory => _statusHistory;

    public Money TotalAmount => Money.FromMinor(TotalAmountMinor, Currency);

    public bool IsCancelled => Status == OrderStatus.Cancelled;

    /// <summary>
    /// Creates a pending order from lines already priced against the catalogue.
    /// </summary>
    /// <param name="lines">
    /// Product id, snapshot name, catalogue-resolved unit price and quantity. Duplicate
    /// product ids are merged and their quantities summed (FR-1.8).
    /// </param>
    public static Order Create(
        Guid id,
        string orderNumber,
        Guid customerId,
        IEnumerable<OrderLine> lines,
        string currency,
        DateTimeOffset createdAt,
        string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentNullException.ThrowIfNull(lines);

        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id must not be empty.", nameof(customerId));
        }

        var merged = MergeDuplicateProducts(lines);
        if (merged.Count == 0)
        {
            throw new EmptyOrderException();
        }

        var order = new Order(id, orderNumber.Trim(), customerId, currency, idempotencyKey, createdAt);

        foreach (var line in merged)
        {
            if (!string.Equals(line.UnitPrice.Currency, currency, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Item '{line.ProductName}' is priced in {line.UnitPrice.Currency} " +
                    $"but the order is in {currency}.");
            }

            order._items.Add(OrderItem.Create(
                Guid.CreateVersion7(),
                line.ProductId,
                line.ProductName,
                line.UnitPrice,
                line.Quantity));
        }

        order.RecalculateTotal();

        // FR-1.11: creation is itself an auditable event.
        order._statusHistory.Add(OrderStatusHistory.Record(
            order.Id,
            fromStatus: null,
            toStatus: OrderStatus.Pending,
            Actor.Customer(customerId),
            reason: null,
            createdAt));

        return order;
    }

    /// <summary>
    /// Moves the order to <paramref name="newStatus"/> if the transition matrix permits
    /// it for <paramref name="actor"/>, otherwise throws and leaves the order untouched.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the order is already in <paramref name="newStatus"/>,
    /// which is treated as an idempotent no-op (FR-3.5); otherwise <see langword="true"/>.
    /// </returns>
    public bool TransitionTo(OrderStatus newStatus, Actor actor, DateTimeOffset occurredAt, string? reason = null)
    {
        if (Status == newStatus)
        {
            return false;
        }

        // Validated before any mutation, so a rejected transition cannot leave the
        // aggregate half-changed.
        OrderStatusTransitions.EnsureAllowed(actor.Type, Status, newStatus);

        if (newStatus == OrderStatus.Cancelled)
        {
            ApplyCancellation(actor, reason, occurredAt);
        }

        var previous = Status;
        Status = newStatus;
        UpdatedAt = occurredAt;
        Version++;

        _statusHistory.Add(OrderStatusHistory.Record(Id, previous, newStatus, actor, reason, occurredAt));
        return true;
    }

    /// <summary>
    /// Cancels the order under the policy in specification section 6.3: a customer may
    /// cancel only while pending, an administrator may also cancel while processing and
    /// must supply a reason.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the order was already cancelled, in which case the
    /// original cancellation audit data is left intact (FR-5.7).
    /// </returns>
    public bool Cancel(Actor actor, DateTimeOffset occurredAt, string? reason = null) =>
        TransitionTo(OrderStatus.Cancelled, actor, occurredAt, reason);

    /// <summary>
    /// Promotes a pending order to processing on behalf of the background job (FR-6.2).
    /// </summary>
    public bool PromoteToProcessing(DateTimeOffset occurredAt) =>
        TransitionTo(OrderStatus.Processing, Actor.System, occurredAt);

    /// <summary>
    /// Whether <paramref name="customerId"/> owns this order. Used as a defence-in-depth
    /// check behind the query-level scoping described in section 8.3.
    /// </summary>
    public bool IsOwnedBy(Guid customerId) => CustomerId == customerId;

    private void ApplyCancellation(Actor actor, string? reason, DateTimeOffset occurredAt)
    {
        // FR-5.4: an administrative override is only defensible if it is attributable.
        if (actor.IsAdmin && string.IsNullOrWhiteSpace(reason))
        {
            throw new CancellationReasonRequiredException();
        }

        CancelledAt = occurredAt;
        CancelledBy = actor.Type;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private void RecalculateTotal() =>
        TotalAmountMinor = _items.Sum(item => item.LineTotalMinor);

    private static List<OrderLine> MergeDuplicateProducts(IEnumerable<OrderLine> lines) =>
        [.. lines
            .GroupBy(line => line.ProductId)
            .Select(group => group.First() with
            {
                Quantity = group.Sum(line => line.Quantity)
            })];
}

/// <summary>
/// A requested order line, already priced against the catalogue by the application layer.
/// </summary>
public readonly record struct OrderLine(Guid ProductId, string ProductName, Money UnitPrice, int Quantity);
