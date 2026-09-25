using Microsoft.Extensions.Logging;

namespace OrderProcessing.Infrastructure.Scheduling;

/// <summary>
/// Source-generated log messages for the promotion job.
/// </summary>
/// <remarks>
/// The <c>[LoggerMessage]</c> generator produces strongly-typed, allocation-free
/// delegates and skips argument evaluation entirely when the level is disabled.
/// That matters here because the job logs on every run, several times a run,
/// for the lifetime of the process.
/// </remarks>
internal static partial class PromotionLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Order promotion job started; interval {Interval}, batch size {BatchSize}.")]
    public static partial void JobStarted(ILogger logger, TimeSpan interval, int batchSize);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Order promotion job is disabled by configuration.")]
    public static partial void JobDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Order promotion job stopping.")]
    public static partial void JobStopping(ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Promotion run {RunId} completed: claimed {Claimed}, promoted {Promoted}, " +
                  "failed {Failed}, duration {DurationMs}ms.")]
    public static partial void RunCompleted(
        ILogger logger,
        Guid runId,
        int claimed,
        int promoted,
        int failed,
        double durationMs);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "Promotion run {RunId} found no pending orders ({DurationMs}ms).")]
    public static partial void RunFoundNothing(ILogger logger, Guid runId, double durationMs);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Promotion run {RunId} failed.")]
    public static partial void RunFailed(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Debug,
        Message = "Claimed {ClaimedCount} pending orders for promotion.")]
    public static partial void OrdersClaimed(ILogger logger, int claimedCount);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Error,
        Message = "Failed to record promotion history for order {OrderId}.")]
    public static partial void PromotionHistoryFailed(ILogger logger, Guid orderId, Exception exception);
}
