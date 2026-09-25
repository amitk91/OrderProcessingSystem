using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Api.Contracts;

/// <summary>A requested line in a create-order request.</summary>
/// <remarks>
/// Note the absence of a price field. Prices are resolved from the catalogue, so
/// there is nothing here for a client to tamper with (FR-1.6).
/// </remarks>
public sealed record CreateOrderItemRequest
{
    [Required]
    public Guid ProductId { get; init; }

    [Range(1, 1000, ErrorMessage = "Quantity must be between 1 and 1000.")]
    public int Quantity { get; init; }
}

/// <summary>Request body for placing an order.</summary>
public sealed record CreateOrderRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "An order must contain at least one item.")]
    public IReadOnlyList<CreateOrderItemRequest> Items { get; init; } = [];
}

/// <summary>Request body for an administrative status change.</summary>
public sealed record UpdateOrderStatusRequest
{
    [Required]
    public string Status { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>Request body for cancelling an order.</summary>
/// <remarks>
/// The reason is optional here and enforced for administrators in the domain, so the
/// rule lives with the policy it belongs to rather than being duplicated in a
/// validator that cannot see the caller's role.
/// </remarks>
public sealed record CancelOrderRequest
{
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>Request body for the development-only token endpoint.</summary>
public sealed record DevTokenRequest
{
    [Required]
    public Guid UserId { get; init; }

    [Required]
    public string Role { get; init; } = "Customer";

    public string Email { get; init; } = "dev@example.com";
}
