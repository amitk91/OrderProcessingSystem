using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// A single product line within an order (specification section 5.1).
/// </summary>
/// <remarks>
/// <see cref="ProductName"/> and <see cref="UnitPriceMinor"/> are <em>snapshots</em>
/// taken when the order was placed, not a join to the live catalogue. If a
/// catalogue price later changes, historical orders must still show what the
/// customer actually agreed to pay (specification section 5.2).
/// </remarks>
public sealed class OrderItem
{
    private OrderItem()
    {
        // EF Core materialisation.
        ProductName = null!;
        Currency = null!;
    }

    private OrderItem(Guid id, Guid productId, string productName, Money unitPrice, int quantity)
    {
        Id = id;
        ProductId = productId;
        ProductName = productName;
        UnitPriceMinor = unitPrice.AmountMinor;
        Currency = unitPrice.Currency;
        Quantity = quantity;
        LineTotalMinor = (unitPrice * quantity).AmountMinor;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; }

    public long UnitPriceMinor { get; private set; }

    public string Currency { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>
    /// Persisted rather than computed on read, so the stored order remains
    /// self-describing and auditable without recomputation.
    /// </summary>
    public long LineTotalMinor { get; private set; }

    public Money UnitPrice => Money.FromMinor(UnitPriceMinor, Currency);

    public Money LineTotal => Money.FromMinor(LineTotalMinor, Currency);

    internal static OrderItem Create(Guid id, Guid productId, string productName, Money unitPrice, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);

        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product id must not be empty.", nameof(productId));
        }

        if (unitPrice.IsNegative)
        {
            throw new ArgumentException("Unit price must not be negative.", nameof(unitPrice));
        }

        return new OrderItem(id, productId, productName.Trim(), unitPrice, quantity);
    }
}
