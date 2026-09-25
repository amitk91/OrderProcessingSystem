using System.Globalization;

namespace OrderProcessing.Domain.Common;

/// <summary>
/// A monetary amount in a single currency, stored as integer minor units
/// (cents) per specification section 5.4.
/// </summary>
/// <remarks>
/// Minor units rather than <see cref="decimal"/> for three reasons:
/// <list type="bullet">
///   <item>SQLite has no native decimal type; EF Core stores decimals as TEXT,
///   which sorts lexicographically ("9.99" after "100.00") and would silently
///   break the sort-by-total requirement (FR-4.7).</item>
///   <item>Integer arithmetic is exact, so summing line totals cannot drift.</item>
///   <item>It matches what payment processors do, so the representation survives
///   contact with a real PSP.</item>
/// </list>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    private Money(long amountMinor, string currency)
    {
        AmountMinor = amountMinor;
        Currency = currency;
    }

    /// <summary>The amount in minor units. $49.99 is stored as 4999.</summary>
    public long AmountMinor { get; }

    /// <summary>ISO 4217 currency code, upper-case (for example "USD").</summary>
    public string Currency { get; }

    /// <summary>
    /// The amount in major units, for display and API responses only.
    /// Business logic operates on <see cref="AmountMinor"/>.
    /// </summary>
    public decimal Amount => AmountMinor / 100m;

    public static Money Zero(string currency) => FromMinor(0, currency);

    public static Money FromMinor(long amountMinor, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (currency.Length != 3)
        {
            throw new ArgumentException(
                $"Currency must be a three-letter ISO 4217 code, but was '{currency}'.",
                nameof(currency));
        }

        return new Money(amountMinor, currency.ToUpperInvariant());
    }

    /// <summary>
    /// Creates an amount from major units. Rejects sub-cent precision rather than
    /// rounding it away, so a caller cannot silently lose a fraction of a cent.
    /// </summary>
    public static Money FromDecimal(decimal amount, string currency)
    {
        var minor = amount * 100m;

        if (decimal.Truncate(minor) != minor)
        {
            throw new ArgumentException(
                $"Amount {amount} has sub-minor-unit precision and cannot be represented exactly.",
                nameof(amount));
        }

        return FromMinor((long)minor, currency);
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(AmountMinor + other.AmountMinor), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(AmountMinor - other.AmountMinor), Currency);
    }

    /// <summary>
    /// Multiplies by a whole number, as when extending a unit price by a quantity.
    /// Integer-only by design: there is no fractional multiplication that could
    /// introduce a rounding decision the caller has not made explicitly.
    /// </summary>
    public Money MultiplyBy(int factor) =>
        new(checked(AmountMinor * factor), Currency);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money money, int factor) => money.MultiplyBy(factor);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public bool IsNegative => AmountMinor < 0;

    public bool IsZero => AmountMinor == 0;

    /// <summary>
    /// Sums amounts that must all share a currency. Returns zero in
    /// <paramref name="currency"/> for an empty sequence, which is why the currency
    /// is required rather than inferred from the first element.
    /// </summary>
    public static Money Sum(IEnumerable<Money> amounts, string currency)
    {
        ArgumentNullException.ThrowIfNull(amounts);

        var total = Zero(currency);
        foreach (var amount in amounts)
        {
            total = total.Add(amount);
        }

        return total;
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return AmountMinor.CompareTo(other.AmountMinor);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot operate on amounts in different currencies: '{Currency}' and '{other.Currency}'.");
        }
    }
}
