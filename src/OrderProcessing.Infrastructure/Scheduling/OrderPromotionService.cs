using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Scheduling;

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
/// Promotes pending orders to processing, in bounded batches
/// (specification section 9.4).
/// </summary>
/// <remarks>
/// Separated from the hosted service so the work can be invoked directly in tests
/// without starting a host or waiting on a timer.
///
/// The status-history rows are written here rather than in the claim statement because
/// the claim is deliberately a single atomic SQL statement; history is appended for
/// exactly the orders the claim reported, so the two cannot diverge.
/// </remarks>
public sealed class OrderPromotionService(
    OrderProcessingDbContext dbContext,
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

            var now = timeProvider.GetUtcNow();
            var claimedIds = await claimer.ClaimPendingOrdersAsync(batchSize, now, cancellationToken);

            if (claimedIds.Count == 0)
            {
                break;
            }

            var (promoted, failed) = await RecordPromotionsAsync(claimedIds, now, cancellationToken);
            result = result.Add(claimedIds.Count, promoted, failed);

            // A short batch means the backlog is drained; stop rather than spin.
            if (claimedIds.Count < batchSize)
            {
                break;
            }
        }

        return result;
    }

    private async Task<(int Promoted, int Failed)> RecordPromotionsAsync(
        IReadOnlyList<Guid> claimedIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var historyEntries = new List<OrderStatusHistory>(claimedIds.Count);
        var failed = 0;

        foreach (var orderId in claimedIds)
        {
            try
            {
                historyEntries.Add(OrderStatusHistory.ForSystemPromotion(orderId, occurredAt));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FR-6.7: one bad order must not cost the rest of the batch.
                failed++;
                PromotionLog.PromotionHistoryFailed(logger, orderId, ex);
            }
        }

        if (historyEntries.Count == 0)
        {
            return (0, failed);
        }

        dbContext.OrderStatusHistory.AddRange(historyEntries);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Claimed rows were updated by raw SQL, so anything already tracked holds a
        // stale status. Clearing the change tracker prevents a later read in this
        // scope returning Pending for an order that is now Processing.
        dbContext.ChangeTracker.Clear();

        return (historyEntries.Count, failed);
    }
}
