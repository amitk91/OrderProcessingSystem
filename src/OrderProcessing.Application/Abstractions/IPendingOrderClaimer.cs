namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// Claims pending orders for promotion by the background job
/// (specification section 9.3).
/// </summary>
/// <remarks>
/// This is a port because the safe-claiming mechanism is database-specific: SQLite
/// relies on write serialisation and an atomic conditional <c>UPDATE</c>, whereas
/// PostgreSQL uses <c>FOR UPDATE SKIP LOCKED</c>. Both satisfy the same contract, so
/// the scheduler and its tests are unchanged when the provider is swapped.
///
/// Implementations must guarantee that concurrent callers never receive the same
/// order (FR-6.4), and that an order cancelled between selection and commit is not
/// promoted (FR-6.6).
/// </remarks>
public interface IPendingOrderClaimer
{
    /// <summary>
    /// Atomically transitions up to <paramref name="batchSize"/> pending orders to
    /// processing and returns the ones actually claimed.
    /// </summary>
    /// <returns>
    /// The claimed order ids. Empty when there is no pending work, which the caller
    /// uses as the signal to stop looping.
    /// </returns>
    Task<IReadOnlyList<Guid>> ClaimPendingOrdersAsync(
        int batchSize,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
