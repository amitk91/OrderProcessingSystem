using OrderProcessing.Domain.Common;
using Shouldly;

namespace OrderProcessing.Domain.Tests.Common;

/// <summary>
/// Verifies the money representation chosen in specification section 5.4.
/// The sorting and precision tests are the ones that matter: they pin the two
/// defects that motivated integer minor units in the first place.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void FromDecimal_converts_major_units_to_minor_units()
    {
        var money = Money.FromDecimal(49.99m, "USD");

        money.AmountMinor.ShouldBe(4999);
        money.Amount.ShouldBe(49.99m);
        money.Currency.ShouldBe("USD");
    }

    [Fact]
    public void FromMinor_round_trips_through_Amount()
    {
        Money.FromMinor(4999, "USD").Amount.ShouldBe(49.99m);
        Money.FromMinor(0, "USD").Amount.ShouldBe(0m);
        Money.FromMinor(-250, "USD").Amount.ShouldBe(-2.50m);
    }

    [Fact]
    public void FromDecimal_rejects_sub_cent_precision_rather_than_rounding_it_away()
    {
        // Silently rounding would lose money without telling anyone.
        Should.Throw<ArgumentException>(() => Money.FromDecimal(10.005m, "USD"));
    }

    [Fact]
    public void Currency_is_normalised_to_upper_case()
    {
        Money.FromMinor(100, "usd").Currency.ShouldBe("USD");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("US")]
    [InlineData("USDD")]
    public void Invalid_currency_codes_are_rejected(string currency)
    {
        Should.Throw<ArgumentException>(() => Money.FromMinor(100, currency));
    }

    [Fact]
    public void Addition_and_subtraction_are_exact()
    {
        var a = Money.FromDecimal(0.10m, "USD");
        var b = Money.FromDecimal(0.20m, "USD");

        // The canonical floating-point failure: 0.1 + 0.2 != 0.3.
        // Integer minor units make this exact by construction.
        (a + b).AmountMinor.ShouldBe(30);
        (a + b).Amount.ShouldBe(0.30m);
        (b - a).Amount.ShouldBe(0.10m);
    }

    [Fact]
    public void Multiplication_extends_a_unit_price_by_a_quantity()
    {
        var unitPrice = Money.FromDecimal(49.99m, "USD");

        (unitPrice * 3).Amount.ShouldBe(149.97m);
    }

    [Fact]
    public void Summing_many_small_amounts_does_not_drift()
    {
        // A thousand additions of $0.01 must be exactly $10.00. With doubles this
        // accumulates visible error; with minor units it cannot.
        var cent = Money.FromDecimal(0.01m, "USD");
        var total = Money.Sum(Enumerable.Repeat(cent, 1000), "USD");

        total.AmountMinor.ShouldBe(1000);
        total.Amount.ShouldBe(10.00m);
    }

    [Fact]
    public void Sum_of_an_empty_sequence_is_zero_in_the_requested_currency()
    {
        var total = Money.Sum([], "EUR");

        total.IsZero.ShouldBeTrue();
        total.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Arithmetic_across_currencies_throws_rather_than_producing_a_meaningless_result()
    {
        var usd = Money.FromDecimal(10m, "USD");
        var eur = Money.FromDecimal(10m, "EUR");

        Should.Throw<InvalidOperationException>(() => usd + eur);
        Should.Throw<InvalidOperationException>(() => usd - eur);
        Should.Throw<InvalidOperationException>(() => usd.CompareTo(eur));
    }

    [Fact]
    public void Amounts_sort_numerically_and_not_lexicographically()
    {
        // The defect that drove this design: as TEXT, "9.99" sorts after "100.00",
        // which would silently break sort-by-total (FR-4.7) and its pagination.
        var amounts = new[]
        {
            Money.FromDecimal(100.00m, "USD"),
            Money.FromDecimal(9.99m, "USD"),
            Money.FromDecimal(20.00m, "USD"),
            Money.FromDecimal(1000.00m, "USD")
        };

        var sorted = amounts.OrderBy(m => m.AmountMinor).Select(m => m.Amount).ToArray();

        sorted.ShouldBe([9.99m, 20.00m, 100.00m, 1000.00m]);
    }

    [Fact]
    public void Comparison_operators_order_amounts_correctly()
    {
        var small = Money.FromDecimal(9.99m, "USD");
        var large = Money.FromDecimal(100.00m, "USD");
        var sameAsSmall = Money.FromDecimal(9.99m, "USD");

        (small < large).ShouldBeTrue();
        (large > small).ShouldBeTrue();
        (small <= sameAsSmall).ShouldBeTrue();
        (small >= sameAsSmall).ShouldBeTrue();
        (large >= small).ShouldBeTrue();
        (large < small).ShouldBeFalse();
    }

    [Fact]
    public void Equality_accounts_for_both_amount_and_currency()
    {
        Money.FromMinor(1000, "USD").ShouldBe(Money.FromMinor(1000, "USD"));
        Money.FromMinor(1000, "USD").ShouldNotBe(Money.FromMinor(1000, "EUR"));
        Money.FromMinor(1000, "USD").ShouldNotBe(Money.FromMinor(2000, "USD"));
    }

    [Fact]
    public void Negative_and_zero_amounts_are_reported_correctly()
    {
        Money.FromDecimal(-1m, "USD").IsNegative.ShouldBeTrue();
        Money.Zero("USD").IsZero.ShouldBeTrue();
        Money.Zero("USD").IsNegative.ShouldBeFalse();
    }

    [Fact]
    public void Overflow_throws_rather_than_wrapping_silently()
    {
        var huge = Money.FromMinor(long.MaxValue, "USD");

        Should.Throw<OverflowException>(() => huge + Money.FromMinor(1, "USD"));
        Should.Throw<OverflowException>(() => huge * 2);
    }

    [Fact]
    public void ToString_is_culture_invariant()
    {
        // Guards against a comma decimal separator appearing in logs or responses
        // on a machine with a European locale.
        Money.FromDecimal(1234.50m, "USD").ToString().ShouldBe("1234.50 USD");
    }
}
