using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Infrastructure.Scheduling;

/// <summary>
/// Configuration for the pending-order promotion job (specification section 9).
/// </summary>
public sealed class OrderPromotionOptions
{
    public const string SectionName = "OrderPromotion";

    /// <summary>
    /// How often the job runs. The brief specifies five minutes; it is configurable
    /// so tests and demos need not wait that long.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Orders claimed per transaction (FR-6.3). Bounds both memory and transaction
    /// duration, so a large backlog does not become one long-running write.
    /// </summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Ceiling on batches per run. Prevents a single run monopolising the database
    /// when a very large backlog has accumulated; the remainder is picked up next tick.
    /// </summary>
    [Range(1, 1_000)]
    public int MaxBatchesPerRun { get; set; } = 50;

    /// <summary>
    /// Whether the job runs at all. Disabled in integration tests so the scheduler
    /// cannot race the assertions of tests that are not about it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Delay before the first run, giving the host time to finish starting.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);
}
