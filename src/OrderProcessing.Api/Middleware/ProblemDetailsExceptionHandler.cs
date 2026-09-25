using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Api.Middleware;

/// <summary>
/// Translates domain and application exceptions into RFC 7807 responses
/// (specification section 11.3).
/// </summary>
/// <remarks>
/// Centralised so controllers contain no try/catch and no status-code decisions:
/// an exception type maps to exactly one status code, in one place. Unexpected
/// exceptions become a 500 with no detail leaked to the caller, while the full
/// exception is logged against the correlation id.
/// </remarks>
internal sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    private const string ErrorTypeBase = "https://orderprocessing/errors/";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var correlationId = httpContext.TraceIdentifier;
        var (status, title, errorCode) = Map(exception);

        if (status >= (int)HttpStatusCode.InternalServerError)
        {
            ApiLog.UnhandledException(logger, correlationId, exception);
        }
        else
        {
            ApiLog.RequestRejected(logger, correlationId, errorCode, exception.Message);
        }

        var problemDetails = new ProblemDetails
        {
            Type = ErrorTypeBase + errorCode,
            Title = title,
            Status = status,
            Instance = httpContext.Request.Path,

            // A 500 must not leak internal detail; everything else is safe to explain.
            Detail = status >= (int)HttpStatusCode.InternalServerError
                ? "An unexpected error occurred."
                : exception.Message
        };

        problemDetails.Extensions["correlationId"] = correlationId;

        if (exception is InvalidStatusTransitionException transition)
        {
            // FR-3.3: tell the caller what they could legally have done instead.
            problemDetails.Extensions["attemptedTransition"] =
                $"{transition.From} -> {transition.To}";
            problemDetails.Extensions["permittedTransitions"] =
                transition.Permitted.Select(status => status.ToString().ToUpperInvariant()).ToArray();
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    private static (int Status, string Title, string ErrorCode) Map(Exception exception) =>
        exception switch
        {
            // 404 rather than 403 for an order the caller does not own, so existence
            // is not disclosed (specification section 8.4).
            OrderNotFoundException => (
                (int)HttpStatusCode.NotFound, "Order not found", "order-not-found"),

            ProductNotFoundException => (
                (int)HttpStatusCode.BadRequest, "Product not found", "product-not-found"),

            // 422: the request is well-formed but the product cannot be ordered.
            ProductInactiveException => (
                (int)HttpStatusCode.UnprocessableEntity, "Product unavailable", "product-inactive"),

            IdempotencyKeyConflictException => (
                (int)HttpStatusCode.UnprocessableEntity,
                "Idempotency key reused with a different request",
                "idempotency-key-conflict"),

            // 409: well-formed, but conflicts with current state.
            InvalidStatusTransitionException => (
                (int)HttpStatusCode.Conflict, "Invalid status transition", "invalid-status-transition"),

            CancellationReasonRequiredException => (
                (int)HttpStatusCode.BadRequest, "Cancellation reason required", "cancellation-reason-required"),

            EmptyOrderException => (
                (int)HttpStatusCode.BadRequest, "Order must contain at least one item", "empty-order"),

            DomainException domain => (
                (int)HttpStatusCode.BadRequest, "Request rejected", domain.ErrorCode),

            // Concurrency conflict (specification section 10.2): the caller may retry.
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (
                (int)HttpStatusCode.Conflict,
                "The order was modified concurrently; please retry",
                "concurrency-conflict"),

            _ => ((int)HttpStatusCode.InternalServerError, "An unexpected error occurred", "internal-error")
        };
}
