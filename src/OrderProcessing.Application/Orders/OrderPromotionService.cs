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
/// This is a use case, so it lives in the application layer alongside the others. The
/// split from <c>OrderPromotionBackgroundService</c> is deliberate and is the clearest
/// illustration of the layering:
/// <list type="bullet">
///   <item><b>Infrastructure</b> decides <em>when</em> to run — timers, host lifetime,
///   service scopes.</item>
///   <item><b>Application</b> (this type) decides <em>what</em> a run does — batching,
///   failure isolation, audit entries.</item>
///   <item><b>Infrastructure</b> again decides <em>how</em> to claim safely, behind
///   <see cref="IPendingOrderClaimer"/>, because that is provider-specific.</item>
/// </list>
/// The practical payoff is that a run can be invoked directly in tests, with no host
/// and no waiting on a clock.
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

        orders.AddStatusHistory(historyEntries);
        await orders.SaveChangesAsync(cancellationToken);

        return (historyEntries.Count, failed);
    }
}
