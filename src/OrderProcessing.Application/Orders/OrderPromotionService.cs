using Microsoft.Extensions.Logging;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Application.Orders;

/// <summary>
/// Outcome of a single promotion run, used for logging and metrics (FR-6.8).
/// </summary>
public readonly record struct PromotionRunResult(int Claimed, int Promoted, int Failed)
{
    public static PromotionRunResult Empty => new(0, 0, 0);

    public PromotionRunResult Add(int claimed, int promoted, int failed) =>
        new(Claimed + claimed, Promoted + promoted, Failed + failed);
}

/// <summary>
/// Promotes pending orders to processing in bounded batches (FR-6, specification
/// section 9.4).
/// </summary>
/// <remarks>
/// <para>Each batch runs in one transaction with three steps:</para>
/// <list type="number">
///   <item><b>Claim</b> — take an exclusive lock on up to <c>batchSize</c> pending
///   orders. A lock, not a transition: nothing about the order changes yet.</item>
///   <item><b>Promote</b> — load those aggregates and call
///   <see cref="Order.PromoteToProcessing"/>, so the transition is validated by the
///   same matrix the API uses and writes its own audit entry.</item>
///   <item><b>Commit</b> — status and history persist together, or neither does.</item>
/// </list>
///
/// <para>The earlier design collapsed steps 1 and 2 into a single SQL statement. It
/// claimed atomically and behaved correctly, but it restated the transition rule in
/// SQL and committed the status change before the audit entry was written — so an
/// interrupted run could leave an order promoted with no history. Splitting the lock
/// from the transition removes both problems without giving up exactly-once claiming.</para>
/// </remarks>
public sealed class OrderPromotionService(
    IOrderRepository orders,
    IPendingOrderClaimer claimer,
    TimeProvider timeProvider,
    ILogger<OrderPromotionService> logger)
{
    public async Task<PromotionRunResult> PromotePendingOrdersAsync(
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        var result = PromotionRunResult.Empty;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchResult = await PromoteOneBatchAsync(batchSize, cancellationToken);
            result = result.Add(batchResult.Claimed, batchResult.Promoted, batchResult.Failed);

            // Nothing claimed, or a short batch: the backlog is drained.
            if (batchResult.Claimed < batchSize)
            {
                break;
            }
        }

        return result;
    }

    private async Task<PromotionRunResult> PromoteOneBatchAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        // One transaction per batch: bounds how long locks are held, and keeps a large
        // backlog from becoming one long-running write (specification section 9.4).
        await using var transaction = await orders.BeginTransactionAsync(cancellationToken);

        var claimedIds = await claimer.ClaimPendingOrdersAsync(batchSize, cancellationToken);
        if (claimedIds.Count == 0)
        {
            return PromotionRunResult.Empty;
        }

        var claimed = await orders.LoadForPromotionAsync(claimedIds, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var promoted = 0;
        var failed = 0;

        foreach (var order in claimed)
        {
            try
            {
                // The single point of enforcement. The matrix permits System to move an
                // order from Pending to Processing and nothing else; anything no longer
                // pending is rejected here rather than silently overwritten.
                if (order.PromoteToProcessing(now))
                {
                    promoted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FR-6.7: one bad order must not cost the rest of the batch.
                failed++;
                PromotionLog.PromotionFailed(logger, order.Id, ex);
            }
        }

        await orders.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PromotionRunResult(claimedIds.Count, promoted, failed);
    }
}
