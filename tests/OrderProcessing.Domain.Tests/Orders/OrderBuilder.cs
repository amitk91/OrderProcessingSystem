using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Domain.Tests.Orders;

/// <summary>
/// Builder for <see cref="Order"/> instances in tests (specification section 12.3).
/// Defaults are valid, so each test overrides only the aspect it is about and the
/// intent of the test stays visible.
/// </summary>
internal sealed class OrderBuilder
{
    private static readonly DateTimeOffset DefaultCreatedAt =
        new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly List<OrderLine> _lines = [];
    private Guid _customerId = Guid.CreateVersion7();
    private string _currency = "USD";
    private string _orderNumber = "ORD-2026-000001";
    private string? _idempotencyKey;
    private DateTimeOffset _createdAt = DefaultCreatedAt;

    public OrderBuilder ForCustomer(Guid customerId)
    {
        _customerId = customerId;
        return this;
    }

    public OrderBuilder WithOrderNumber(string orderNumber)
    {
        _orderNumber = orderNumber;
        return this;
    }

    public OrderBuilder WithCurrency(string currency)
    {
        _currency = currency;
        return this;
    }

    public OrderBuilder WithIdempotencyKey(string? key)
    {
        _idempotencyKey = key;
        return this;
    }

    public OrderBuilder CreatedAt(DateTimeOffset createdAt)
    {
        _createdAt = createdAt;
        return this;
    }

    public OrderBuilder WithItem(decimal unitPrice, int quantity, string? name = null, Guid? productId = null)
    {
        _lines.Add(new OrderLine(
            productId ?? Guid.CreateVersion7(),
            name ?? $"Product {_lines.Count + 1}",
            Money.FromDecimal(unitPrice, _currency),
            quantity));

        return this;
    }

    public OrderBuilder WithLine(OrderLine line)
    {
        _lines.Add(line);
        return this;
    }

    public Order Build()
    {
        if (_lines.Count == 0)
        {
            WithItem(10.00m, 1);
        }

        return Order.Create(
            Guid.CreateVersion7(),
            _orderNumber,
            _customerId,
            _lines,
            _createdAt,
            _idempotencyKey);
    }

    /// <summary>
    /// Builds an order already advanced to <paramref name="status"/> by walking the
    /// legal transition path, so the resulting order is one the system could actually
    /// have produced rather than an impossible state forced into place.
    /// </summary>
    public Order BuildInStatus(OrderStatus status, Guid? adminId = null)
    {
        var order = Build();
        if (status == OrderStatus.Pending)
        {
            return order;
        }

        var admin = Actor.Admin(adminId ?? Guid.CreateVersion7());
        var at = _createdAt.AddMinutes(1);

        switch (status)
        {
            case OrderStatus.Processing:
                order.TransitionTo(OrderStatus.Processing, admin, at);
                break;

            case OrderStatus.Shipped:
                order.TransitionTo(OrderStatus.Processing, admin, at);
                order.TransitionTo(OrderStatus.Shipped, admin, at.AddMinutes(1));
                break;

            case OrderStatus.Delivered:
                order.TransitionTo(OrderStatus.Processing, admin, at);
                order.TransitionTo(OrderStatus.Shipped, admin, at.AddMinutes(1));
                order.TransitionTo(OrderStatus.Delivered, admin, at.AddMinutes(2));
                break;

            case OrderStatus.Cancelled:
                order.Cancel(admin, at, "Test setup");
                break;

            case OrderStatus.Pending:
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported status.");
        }

        return order;
    }
}
