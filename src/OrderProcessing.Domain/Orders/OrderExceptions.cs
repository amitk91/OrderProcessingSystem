using OrderProcessing.Domain.Common;

namespace OrderProcessing.Domain.Orders;

/// <summary>
/// Raised when an order is created with no items (FR-1.2).
/// </summary>
public sealed class EmptyOrderException() : DomainException("An order must contain at least one item.")
{
    public override string ErrorCode => "empty-order";
}

/// <summary>
/// Raised when an administrator attempts to cancel without giving a reason (FR-5.4).
/// </summary>
public sealed class CancellationReasonRequiredException()
    : DomainException("A reason is required when an administrator cancels an order.")
{
    public override string ErrorCode => "cancellation-reason-required";
}

/// <summary>
/// Raised when the requested items are not all priced in the same currency (FR-1.5a).
/// </summary>
/// <remarks>
/// <para>An order carries a single total, so every line must share one currency. Mixing
/// them is not an arithmetic problem to be solved by conversion — it is a request the
/// system cannot satisfy, because converting would require a rate, a rate source, and
/// a decision about who bears the spread. Multi-currency is out of scope
/// (specification section 1.2), so this is rejected explicitly rather than resolved
/// implicitly.</para>
///
/// <para>A <see cref="DomainException"/> rather than a bare
/// <see cref="InvalidOperationException"/>, because it is a rule a client can violate
/// with a well-formed request. Framework exceptions map to 500; this maps to 422 and
/// names the currencies involved so the caller can act on it.</para>
/// </remarks>
public sealed class MixedCurrencyOrderException(IReadOnlyCollection<string> currencies)
    : DomainException(BuildMessage(currencies))
{
    /// <summary>The distinct currencies found among the requested items, sorted.</summary>
    public IReadOnlyCollection<string> Currencies { get; } = currencies;

    public override string ErrorCode => "mixed-currency-order";

    private static string BuildMessage(IReadOnlyCollection<string> currencies)
    {
        ArgumentNullException.ThrowIfNull(currencies);

        return "All items in an order must share one currency, but the requested items are " +
               $"priced in {string.Join(", ", currencies)}. Place a separate order per currency.";
    }
}
