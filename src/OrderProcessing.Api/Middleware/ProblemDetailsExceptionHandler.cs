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
        var (status, title, errorCode) = ProblemDetailsMapper.Map(exception);

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
}
