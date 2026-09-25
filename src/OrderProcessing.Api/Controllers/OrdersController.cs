using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderProcessing.Api.Contracts;
using OrderProcessing.Api.Security;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Api.Controllers;

/// <summary>
/// Order lifecycle endpoints (specification section 11).
/// </summary>
[ApiController]
[Route("api/v1/orders")]
[Authorize]
[Produces("application/json")]
public sealed class OrdersController(OrderService orderService) : ControllerBase
{
    private const string GetOrderRouteName = "GetOrderById";
    /// <summary>Places an order for the authenticated customer (FR-1).</summary>
    /// <remarks>
    /// Supply an <c>Idempotency-Key</c> header to make retries safe: repeating a
    /// request with the same key returns the original order instead of creating a
    /// duplicate (FR-1.12).
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // FR-1.13: the order is attributed to the token's subject. A customerId in the
        // body would be ignored, which is why the request contract does not have one.
        var command = new CreateOrderCommand(
            User.GetUserId(),
            [.. request.Items.Select(item => new CreateOrderItemCommand(item.ProductId, item.Quantity))],
            idempotencyKey);

        var result = await orderService.CreateAsync(command, cancellationToken);

        // FR-1.12: a replayed idempotent request returns the original order with 200,
        // since nothing new was created.
        // CreatedAtRoute rather than CreatedAtAction: ASP.NET Core strips the "Async"
        // suffix from action names, so resolving by action name silently fails.
        return result.WasCreated
            ? CreatedAtRoute(GetOrderRouteName, new { id = result.Order.Id }, result.Order)
            : Ok(result.Order);
    }

    /// <summary>Retrieves a single order (FR-2).</summary>
    /// <remarks>
    /// Returns 404 rather than 403 for an order belonging to another customer, so the
    /// response does not disclose that the order exists (specification section 8.4).
    /// </remarks>
    [HttpGet("{id:guid}", Name = GetOrderRouteName)]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await orderService.GetAsync(id, User.ToActor(), cancellationToken));

    /// <summary>Lists orders, newest first by default (FR-4).</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="customerId">Admins only; ignored for customers (FR-4.4).</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Clamped to a maximum of 100 (FR-4.6).</param>
    /// <param name="sortBy"><c>createdAt</c> or <c>totalAmount</c>.</param>
    /// <param name="desc">Sort descending.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<OrderDto>>> ListAsync(
        [FromQuery] string? status,
        [FromQuery] Guid? customerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string sortBy = "createdAt",
        [FromQuery] bool desc = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseStatus(status, out var parsedStatus))
        {
            return Problem(
                title: "Invalid status filter",
                detail: $"'{status}' is not a valid order status. Valid values: " +
                        $"{string.Join(", ", Enum.GetNames<OrderStatus>().Select(n => n.ToUpperInvariant()))}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseSortField(sortBy, out var sortField))
        {
            return Problem(
                title: "Invalid sort field",
                detail: $"'{sortBy}' is not sortable. Valid values: createdAt, totalAmount.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = new ListOrdersQuery(customerId, parsedStatus, page, pageSize, sortField, desc);

        return Ok(await orderService.ListAsync(query, User.ToActor(), cancellationToken));
    }

    /// <summary>Transitions an order's status (FR-3). Administrators only.</summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = AuthConstants.AdminOnlyPolicy)]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderDto>> UpdateStatusAsync(
        Guid id,
        [FromBody] UpdateOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseStatus(request.Status, out var newStatus) || newStatus is null)
        {
            return Problem(
                title: "Invalid status",
                detail: $"'{request.Status}' is not a valid order status.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var order = await orderService.TransitionAsync(
            id, newStatus.Value, User.ToActor(), request.Reason, cancellationToken);

        return Ok(order);
    }

    /// <summary>Cancels an order (FR-5).</summary>
    /// <remarks>
    /// A customer may cancel only their own pending order; an administrator may also
    /// cancel one that is processing, and must give a reason
    /// (specification section 6.3).
    /// </remarks>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderDto>> CancelAsync(
        Guid id,
        [FromBody] CancelOrderRequest? request,
        CancellationToken cancellationToken) =>
        Ok(await orderService.CancelAsync(id, User.ToActor(), request?.Reason, cancellationToken));

    private static bool TryParseStatus(string? value, out OrderStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<OrderStatus>(value, ignoreCase: true, out var parsed))
        {
            status = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseSortField(string? value, out OrderSortField field)
    {
        field = OrderSortField.CreatedAt;

        return string.IsNullOrWhiteSpace(value)
            || Enum.TryParse(value, ignoreCase: true, out field);
    }
}
