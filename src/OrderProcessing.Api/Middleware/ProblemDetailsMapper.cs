using System.Net;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 7807 status codes and stable error codes
/// (specification section 11.4).
/// </summary>
/// <remarks>
/// Separated from the exception handler so the mapping can be asserted directly, arm by
/// arm, without going over HTTP. That matters because switch arms are order-sensitive:
/// a general arm above a specific one shadows it silently, and the compiler only warns
/// when the shadowing is total.
/// </remarks>
public static class ProblemDetailsMapper
{
    public static (int Status, string Title, string ErrorCode) Map(Exception exception) =>
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

            // 422 as well: valid products, valid quantities, but no single order can
            // span two currencies and converting is out of scope.
            MixedCurrencyOrderException => (
                (int)HttpStatusCode.UnprocessableEntity,
                "Items must share one currency",
                "mixed-currency-order"),

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

            // Concurrency conflict (specification section 10.2): the caller may retry.
            // Must precede the general DomainException arm, which it derives from.
            ConcurrencyConflictException => (
                (int)HttpStatusCode.Conflict,
                "The order was modified concurrently; please retry",
                "concurrency-conflict"),

            // These signal a lost race during order creation and are normally handled
            // inside the application layer. Reaching here means the bounded retry was
            // exhausted, so report a conflict the caller can retry.
            DuplicateIdempotencyKeyException or DuplicateOrderNumberException => (
                (int)HttpStatusCode.Conflict,
                "The request conflicted with a concurrent one; please retry",
                "write-conflict"),

            DomainException domain => (
                (int)HttpStatusCode.BadRequest, "Request rejected", domain.ErrorCode),

            // A unique index rejected a write the application layer did not anticipate.
            // Reported as a conflict rather than a 500 because that is what it is — two
            // requests contended for the same value — and a caller can usefully retry.
            // Reaching here still indicates a gap worth investigating in the logs.
            DbUpdateException => (
                (int)HttpStatusCode.Conflict,
                "The request conflicted with an existing record; please retry",
                "write-conflict"),

            _ => ((int)HttpStatusCode.InternalServerError, "An unexpected error occurred", "internal-error")
        };
}
