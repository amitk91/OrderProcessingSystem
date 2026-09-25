namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// Claims pending orders so a worker may promote them (specification section 9.3).
/// </summary>
/// <remarks>
/// <para><b>A claim is a lock, not a transition.</b> This port acquires the exclusive
/// right to work on a set of orders for the duration of the caller's transaction; it
/// does not change their status. Promotion is applied afterwards through
/// <c>Order.PromoteToProcessing</c>, so the transition matrix stays enforced in one
/// place.</para>
///
/// <para>An earlier design folded both concerns into a single
/// <c>UPDATE … SET Status = 'Processing' … RETURNING Id</c>. That was atomic and
/// behaved correctly, but it duplicated the transition rule in SQL and — more
/// seriously — committed the status change independently of the audit entry, so a
/// crash in between left an order promoted with no history row.</para>
///
/// <para>A port because the locking primitive is provider-specific: PostgreSQL uses
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c>, while SQLite has no such clause and takes
/// the lock by writing a lease marker. Both satisfy the same contract.</para>
///
/// <para><b>Contract.</b> Implementations must guarantee that two callers in concurrent
/// transactions never receive the same order (FR-6.4), that only orders currently
/// pending are returned, and that an abandoned (uncommitted) claim leaves no trace.</para>
/// </remarks>
public interface IPendingOrderClaimer
{
    /// <summary>
    /// Locks up to <paramref name="batchSize"/> pending orders for the current
    /// transaction and returns their identifiers.
    /// </summary>
    /// <remarks>
    /// Must be called inside a transaction opened via
    /// <see cref="IOrderRepository.BeginTransactionAsync"/>; the claim is released when
    /// that transaction commits or rolls back.
    /// </remarks>
    /// <returns>
    /// The claimed order ids. Empty when there is no pending work, which the caller
    /// uses as the signal to stop looping.
    /// </returns>
    Task<IReadOnlyList<Guid>> ClaimPendingOrdersAsync(
        int batchSize,
        CancellationToken cancellationToken = default);
}
