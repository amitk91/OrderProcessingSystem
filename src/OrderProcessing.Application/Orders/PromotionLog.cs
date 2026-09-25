using Microsoft.Extensions.Logging;

namespace OrderProcessing.Application.Orders;

/// <summary>
/// Source-generated log messages for the promotion use case.
/// </summary>
/// <remarks>
/// The <c>[LoggerMessage]</c> generator produces strongly-typed, allocation-free
/// delegates and skips argument evaluation entirely when the level is disabled. That
/// matters here because the job logs on every run, for the lifetime of the process.
/// </remarks>
internal static partial class PromotionLog
{
    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Error,
        Message = "Failed to record promotion history for order {OrderId}.")]
    public static partial void PromotionHistoryFailed(ILogger logger, Guid orderId, Exception exception);
}
