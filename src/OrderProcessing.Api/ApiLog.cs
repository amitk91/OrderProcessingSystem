namespace OrderProcessing.Api;

/// <summary>
/// Source-generated log messages for the API layer.
/// </summary>
internal static partial class ApiLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Error,
        Message = "Unhandled exception for request {CorrelationId}.")]
    public static partial void UnhandledException(ILogger logger, string correlationId, Exception exception);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Request {CorrelationId} rejected ({ErrorCode}): {Reason}")]
    public static partial void RequestRejected(
        ILogger logger,
        string correlationId,
        string errorCode,
        string reason);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Database created and seeded at {ConnectionString}.")]
    public static partial void DatabaseReady(ILogger logger, string connectionString);
}
