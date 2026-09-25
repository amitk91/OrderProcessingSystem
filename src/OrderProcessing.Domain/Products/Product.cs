using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Products;

/// <summary>
/// A catalogue item (specification section 5.1).
/// </summary>
/// <remarks>
/// The unit price here is authoritative. Order creation resolves prices from the
/// catalogue and ignores any price supplied by the client (FR-1.5, FR-1.6), which
/// is what prevents price tampering.
/// </remarks>
public sealed class Product
{
    private Product()
    {
        // EF Core materialisation.
        Sku = null!;
        Name = null!;
        Currency = null!;
    }

    private Product(Guid id, string sku, string name, Money unitPrice, bool isActive)
    {
        Id = id;
        Sku = sku;
        Name = name;
        UnitPriceMinor = unitPrice.AmountMinor;
        Currency = unitPrice.Currency;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; }

    public string Name { get; private set; }

    /// <summary>Price in minor units. See <see cref="UnitPrice"/> for the typed view.</summary>
    public long UnitPriceMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>Inactive products cannot be ordered (FR-1.3).</summary>
    public bool IsActive { get; private set; }

    public Money UnitPrice => Money.FromMinor(UnitPriceMinor, Currency);

    public static Product Create(Guid id, string sku, string name, Money unitPrice, bool isActive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Product id must not be empty.", nameof(id));
        }

        if (unitPrice.IsNegative)
        {
            throw new ArgumentException("Product price must not be negative.", nameof(unitPrice));
        }

        return new Product(id, sku.Trim(), name.Trim(), unitPrice, isActive);
    }

    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Changes the catalogue price. Existing orders are unaffected because they hold
    /// a price snapshot taken at the time of ordering (specification section 5.2).
    /// </summary>
    public void ChangePrice(Money newPrice)
    {
        if (newPrice.IsNegative)
        {
            throw new ArgumentException("Product price must not be negative.", nameof(newPrice));
        }

        UnitPriceMinor = newPrice.AmountMinor;
        Currency = newPrice.Currency;
    }
}
